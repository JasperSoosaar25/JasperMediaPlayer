using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Media = LibVLCSharp.Shared.Media;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace JasperMediaPlayer;

public sealed partial class MainWindow : Window
{
    private static readonly Regex HlsUriAttributeRegex = new("URI=\"(?<uri>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HlsBandwidthRegex = new("BANDWIDTH=(?<bandwidth>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HlsResolutionRegex = new("RESOLUTION=(?<width>\\d+)x(?<height>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HlsCodecsRegex = new("CODECS=\"(?<codecs>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Windows.Media.Playback.MediaPlayer _nativeStreamPlayer;
    private readonly DispatcherTimer _timer;
    private readonly HttpClient _httpClient;
    private Media? _currentMedia;
    private Uri? _nativeStreamUri;
    private string? _temporaryHlsPath;
    private bool _isDraggingSeek;
    private bool _isUpdatingSeekFromPlayer;
    private bool _playbackFailedDuringAttempt;
    private bool _currentSourceIsNetwork;
    private bool _nativeHasMedia;
    private int _playbackSessionId;
    private DateTimeOffset _dontSnapSeekBackUntil = DateTimeOffset.MinValue;
    private PlaybackBackend _activeBackend = PlaybackBackend.None;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        Core.Initialize();

        _libVlc = new LibVLC(
            "--no-video-title-show",
            "--network-caching=4500",
            "--live-caching=4500",
            "--file-caching=1200",
            "--http-reconnect",
            "--http-continuous",
            "--adaptive-logic=lowest",
            "--avcodec-hw=none",
            "--http-user-agent=Mozilla/5.0 JasperMediaPlayer/1.0 LibVLC",
            "--verbose=2");

        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = (int)VolumeSlider.Value
        };

        VideoView.MediaPlayer = _mediaPlayer;

        _nativeStreamPlayer = new Windows.Media.Playback.MediaPlayer
        {
            AutoPlay = true,
            Volume = Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0)
        };
        NativeStreamView.SetMediaPlayer(_nativeStreamPlayer);

        _nativeStreamPlayer.MediaOpened += NativeStreamPlayer_MediaOpened;
        _nativeStreamPlayer.MediaFailed += NativeStreamPlayer_MediaFailed;
        _nativeStreamPlayer.CurrentStateChanged += NativeStreamPlayer_CurrentStateChanged;

        _httpClient = CreateHttpClient();

        _mediaPlayer.Opening += (_, _) => DispatcherQueue.TryEnqueue(() => SetStatus("Opening media..."));
        _mediaPlayer.Buffering += (_, args) => DispatcherQueue.TryEnqueue(() => SetStatus($"Buffering stream... {args.Cache:0}%"));
        _mediaPlayer.Playing += (_, _) => DispatcherQueue.TryEnqueue(UpdatePlayingUi);
        _mediaPlayer.Paused += (_, _) => DispatcherQueue.TryEnqueue(UpdatePausedUi);
        _mediaPlayer.Stopped += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStoppedUi();
            if (_currentSourceIsNetwork)
            {
                _playbackFailedDuringAttempt = true;
            }
        });
        _mediaPlayer.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStoppedUi();
            if (_currentSourceIsNetwork)
            {
                _playbackFailedDuringAttempt = true;
                SetStatus("Stream ended immediately. Trying another stream mode if available...");
            }
        });
        _mediaPlayer.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _playbackFailedDuringAttempt = true;
            UpdatePausedUi();
            SetStatus("VLC could not play this URL/file. Trying another stream mode if available...");
        });

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Closed += MainWindow_Closed;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 JasperMediaPlayer/1.0 LibVLC");
        return client;
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".flac");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".ogg");
        picker.FileTypeFilter.Add(".wmv");
        picker.FileTypeFilter.Add(".ts");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        PlayPath(file.Path);
    }

    private async void PlayUrlButton_Click(object sender, RoutedEventArgs e)
    {
        await TryPlayUrlFromTextBoxAsync();
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBackend == PlaybackBackend.Native)
        {
            if (!_nativeHasMedia)
            {
                await TryPlayUrlFromTextBoxAsync();
                return;
            }

            // Use MediaPlayer.CurrentState here instead of PlaybackSession.PlaybackState.
            // On some WinUI/Windows App SDK setups, PlaybackState can throw
            // System.InvalidCastException even while the stream is actually playing.
            if (IsNativeStreamPlaying())
            {
                _nativeStreamPlayer.Pause();
            }
            else
            {
                _nativeStreamPlayer.Play();
            }

            return;
        }

        if (_mediaPlayer.Media is null)
        {
            await TryPlayUrlFromTextBoxAsync();
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopNativeStream(clearSource: true);
        _currentSourceIsNetwork = false;
        _mediaPlayer.Stop();
        _activeBackend = PlaybackBackend.None;
        SetStatus("Stopped.");
        UpdateStoppedUi();
    }

    private async Task TryPlayUrlFromTextBoxAsync()
    {
        var url = NormalizeUrl(UrlBox.Text);

        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Paste a stream URL first.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            SetStatus("That URL is not valid. Example: https://example.com/video.m3u8");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp)
        {
            SetStatus("Only http, https, and ftp URLs are supported here.");
            return;
        }

        UrlBox.Text = uri.AbsoluteUri;

        PlayUrlButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        try
        {
            await PlayUrlAsync(uri);
        }
        finally
        {
            PlayUrlButton.IsEnabled = true;
            PlayPauseButton.IsEnabled = true;
        }
    }

    private void PlayNativeHlsStream(Uri uri)
    {
        try
        {
            _playbackSessionId++;
            _playbackFailedDuringAttempt = false;
            _currentSourceIsNetwork = false;
            _activeBackend = PlaybackBackend.Native;
            _nativeHasMedia = true;
            _nativeStreamUri = uri;

            _mediaPlayer.Stop();
            _currentMedia?.Dispose();
            _currentMedia = null;
            TryDeleteTemporaryFile(_temporaryHlsPath);
            _temporaryHlsPath = null;

            VideoView.Visibility = Visibility.Collapsed;
            NativeStreamView.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;

            SetStatus("Opening HLS stream with Windows 11 native renderer...");
            _nativeStreamPlayer.Source = MediaSource.CreateFromUri(uri);
            _nativeStreamPlayer.Play();
        }
        catch (Exception ex)
        {
            _nativeHasMedia = false;
            _activeBackend = PlaybackBackend.None;
            SetStatus($"Could not start HLS stream: {ex.Message}");
        }
    }

    private void StopNativeStream(bool clearSource)
    {
        try
        {
            _nativeStreamPlayer.Pause();
            if (clearSource)
            {
                _nativeStreamPlayer.Source = null;
                _nativeHasMedia = false;
                _nativeStreamUri = null;
            }
        }
        catch
        {
            // Stopping should never crash the player.
        }
    }

    private void NativeStreamPlayer_MediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeBackend != PlaybackBackend.Native)
            {
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            VideoView.Visibility = Visibility.Collapsed;
            NativeStreamView.Visibility = Visibility.Visible;
            UpdatePlayingUi();
            SetStatus("Playing HLS stream with Windows 11 native renderer.");
        });
    }

    private void NativeStreamPlayer_MediaFailed(Windows.Media.Playback.MediaPlayer sender, Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _nativeHasMedia = false;
            _activeBackend = PlaybackBackend.None;
            UpdatePausedUi();
            SetStatus($"Windows could not play this HLS stream: {args.ErrorMessage}");
        });
    }

    private void NativeStreamPlayer_CurrentStateChanged(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeBackend != PlaybackBackend.Native)
            {
                return;
            }

            // Do NOT switch on sender.PlaybackSession.PlaybackState here.
            // Some Windows builds/projected WinRT metadata combinations can throw
            // "System.InvalidCastException: Specified cast is not valid" from that property.
            // CurrentState is the safer state source for this app.
            Windows.Media.Playback.MediaPlayerState state;
            try
            {
                state = sender.CurrentState;
            }
            catch
            {
                // Never let a status event crash the whole player.
                return;
            }

            switch (state)
            {
                case Windows.Media.Playback.MediaPlayerState.Opening:
                    SetStatus("Opening HLS stream...");
                    break;
                case Windows.Media.Playback.MediaPlayerState.Buffering:
                    SetStatus($"Buffering HLS stream... {GetNativeBufferingPercent(sender):0}%");
                    break;
                case Windows.Media.Playback.MediaPlayerState.Playing:
                    UpdatePlayingUi();
                    SetStatus("Playing HLS stream with Windows 11 native renderer.");
                    break;
                case Windows.Media.Playback.MediaPlayerState.Paused:
                    UpdatePausedUi();
                    SetStatus("Paused.");
                    break;
                case Windows.Media.Playback.MediaPlayerState.Stopped:
                case Windows.Media.Playback.MediaPlayerState.Closed:
                    UpdatePausedUi();
                    break;
            }
        });
    }

    private bool IsNativeStreamPlaying()
    {
        try
        {
            return _nativeStreamPlayer.CurrentState == Windows.Media.Playback.MediaPlayerState.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static double GetNativeBufferingPercent(Windows.Media.Playback.MediaPlayer player)
    {
        try
        {
            return Math.Clamp(player.PlaybackSession.BufferingProgress * 100.0, 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private static string NormalizeUrl(string text)
    {
        var url = (text ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(url)
            && !url.Contains("://", StringComparison.Ordinal)
            && url.Contains('.', StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return url;
    }

    private void PlayPath(string path)
    {
        StopNativeStream(clearSource: true);
        NativeStreamView.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
        _activeBackend = PlaybackBackend.Vlc;
        _currentSourceIsNetwork = false;
        var media = new Media(_libVlc, path, FromType.FromPath);
        PlayMedia(media, $"Opening {Path.GetFileName(path)}...", isNetwork: false);
    }

    private async Task PlayUrlAsync(Uri uri)
    {
        // v5 fix: HLS (.m3u8) was the thing getting stuck/black-screening in LibVLC on this WinUI host.
        // Windows 11's native MediaPlayerElement handles HLS directly, so we use it as the stream renderer
        // while keeping LibVLC for normal files and non-HLS URLs.
        if (LooksLikeHls(uri))
        {
            PlayNativeHlsStream(uri);
            return;
        }

        StopNativeStream(clearSource: true);
        NativeStreamView.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
        _activeBackend = PlaybackBackend.Vlc;

        var candidates = await BuildUrlPlaybackCandidatesAsync(uri);

        if (candidates.Count == 0)
        {
            PlayNetworkUrlDirect(uri, "Opening stream URL...");
            return;
        }

        foreach (var candidate in candidates)
        {
            var sessionId = PlayMedia(candidate.Media, candidate.StatusText, candidate.TemporaryPlaylistPath, isNetwork: true);
            var survived = await WaitForNetworkAttemptAsync(sessionId, candidate.WaitSeconds, candidate.RequiresProgress);
            if (survived)
            {
                SetStatus(candidate.SuccessText);
                return;
            }
        }

        SetStatus("The stream opened but never produced playable video. Try a direct .mp4 URL, or another .m3u8 stream.");
    }

    private async Task<List<PlaybackCandidate>> BuildUrlPlaybackCandidatesAsync(Uri uri)
    {
        var candidates = new List<PlaybackCandidate>();

        if (!LooksLikeHls(uri))
        {
            candidates.Add(CreateNetworkCandidate(uri, "Opening stream URL...", "Playing stream.", waitSeconds: 5));
            return candidates;
        }

        // Important fix: do NOT only feed VLC a rewritten local playlist.
        // Some HLS streams start but render black when opened that way. We now try real network URLs first.
        try
        {
            SetStatus("Reading HLS playlist...");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var masterPlaylist = await FetchTextAsync(uri, cancellation.Token);

            if (masterPlaylist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var variants = PickVideoHlsVariants(masterPlaylist, uri);

                foreach (var variant in variants.Take(4))
                {
                    candidates.Add(CreateHlsNetworkCandidate(
                        variant.Uri,
                        $"Opening HLS video variant {FormatVariantLabel(variant)}...",
                        $"Playing HLS stream {FormatVariantLabel(variant)}.",
                        waitSeconds: 8));
                }

                // Keep the original master as a fallback because some streams need VLC to choose tracks itself.
                candidates.Add(CreateHlsNetworkCandidate(uri, "Opening original HLS master playlist...", "Playing HLS stream.", waitSeconds: 8));

                // Last fallback: local rewritten variant playlist. This helps some playlists with weird relative paths.
                var rewriteSource = variants.FirstOrDefault()?.Uri ?? uri;
                var rewriteText = rewriteSource == uri ? masterPlaylist : await FetchTextAsync(rewriteSource, cancellation.Token);
                var temporaryPlaylist = await CreateTemporaryRewrittenHlsPlaylistAsync(rewriteText, rewriteSource, cancellation.Token);
                if (!string.IsNullOrWhiteSpace(temporaryPlaylist))
                {
                    candidates.Add(CreateHlsFileCandidate(temporaryPlaylist, "Opening HLS fallback playlist...", "Playing HLS fallback playlist.", waitSeconds: 8));
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not pre-read HLS playlist, trying VLC direct mode... ({ex.Message})");
        }

        if (candidates.Count == 0)
        {
            candidates.Add(CreateHlsNetworkCandidate(uri, "Opening HLS stream directly...", "Playing HLS stream.", waitSeconds: 8));
        }

        return candidates;
    }

    private void PlayNetworkUrlDirect(Uri uri, string statusText)
    {
        var candidate = CreateNetworkCandidate(uri, statusText, "Playing stream.", waitSeconds: 5);
        PlayMedia(candidate.Media, candidate.StatusText, isNetwork: true);
    }

    private static bool LooksLikeHls(Uri uri)
    {
        return uri.AbsoluteUri.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("m3u", StringComparison.OrdinalIgnoreCase);
    }

    private PlaybackCandidate CreateNetworkCandidate(Uri uri, string statusText, string successText, int waitSeconds)
    {
        var media = new Media(_libVlc, uri.AbsoluteUri, FromType.FromLocation);
        AddCommonNetworkMediaOptions(media);
        return new PlaybackCandidate(media, statusText, successText, null, waitSeconds, true);
    }

    private PlaybackCandidate CreateHlsNetworkCandidate(Uri uri, string statusText, string successText, int waitSeconds)
    {
        var media = new Media(_libVlc, uri.AbsoluteUri, FromType.FromLocation);
        AddCommonNetworkMediaOptions(media);
        AddHlsMediaOptions(media);
        return new PlaybackCandidate(media, statusText, successText, null, waitSeconds, true);
    }

    private PlaybackCandidate CreateHlsFileCandidate(string path, string statusText, string successText, int waitSeconds)
    {
        var media = new Media(_libVlc, path, FromType.FromPath);
        AddCommonNetworkMediaOptions(media);
        AddHlsMediaOptions(media);
        return new PlaybackCandidate(media, statusText, successText, path, waitSeconds, true);
    }

    private void AddCommonNetworkMediaOptions(Media media)
    {
        media.AddOption(":network-caching=4500");
        media.AddOption(":live-caching=4500");
        media.AddOption(":file-caching=1200");
        media.AddOption(":http-reconnect");
        media.AddOption(":http-continuous");
        media.AddOption(":http-user-agent=Mozilla/5.0 JasperMediaPlayer/1.0 LibVLC");
        media.AddOption(":avcodec-hw=none");
    }

    private static void AddHlsMediaOptions(Media media)
    {
        media.AddOption(":demux=adaptive");
        media.AddOption(":adaptive-logic=lowest");
    }

    private async Task<string> FetchTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 JasperMediaPlayer/1.0 LibVLC");
        request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl");
        request.Headers.Accept.ParseAdd("application/x-mpegURL");
        request.Headers.Accept.ParseAdd("audio/mpegurl");
        request.Headers.Accept.ParseAdd("*/*");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static List<HlsVariant> PickVideoHlsVariants(string playlistText, Uri baseUri)
    {
        var lines = playlistText.Replace("\r\n", "\n").Split('\n');
        var variants = new List<HlsVariant>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bandwidth = ParseInt(HlsBandwidthRegex.Match(line), "bandwidth");
            var resolutionMatch = HlsResolutionRegex.Match(line);
            var width = ParseInt(resolutionMatch, "width");
            var height = ParseInt(resolutionMatch, "height");
            var codecs = HlsCodecsRegex.Match(line).Groups["codecs"].Value;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var uriLine = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(uriLine))
                {
                    continue;
                }

                if (uriLine.StartsWith('#'))
                {
                    break;
                }

                var variantUri = new Uri(baseUri, uriLine);

                if (LooksLikeVideoVariant(width, height, codecs))
                {
                    variants.Add(new HlsVariant(variantUri, bandwidth, width, height, codecs));
                }

                break;
            }
        }

        if (variants.Count == 0)
        {
            return variants;
        }

        // Avoid audio-only variants and avoid instantly choosing huge 4K/high bitrate variants.
        return variants
            .OrderBy(v => v.Height <= 0 ? 10_000 : Math.Abs(v.Height - 720))
            .ThenBy(v => v.Bandwidth <= 0 ? int.MaxValue : v.Bandwidth)
            .ToList();
    }

    private static int ParseInt(Match match, string groupName)
    {
        return match.Success && int.TryParse(match.Groups[groupName].Value, out var value) ? value : 0;
    }

    private static bool LooksLikeVideoVariant(int width, int height, string codecs)
    {
        if (width > 0 && height > 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(codecs))
        {
            return true;
        }

        return codecs.Contains("avc", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("hev", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("vp9", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("vp09", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("av01", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("mp4v", StringComparison.OrdinalIgnoreCase)
            || codecs.Contains("theora", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatVariantLabel(HlsVariant variant)
    {
        if (variant.Width > 0 && variant.Height > 0)
        {
            return $"{variant.Width}x{variant.Height}";
        }

        if (variant.Bandwidth > 0)
        {
            return $"{variant.Bandwidth / 1000} kbps";
        }

        return "video";
    }

    private async Task<string?> CreateTemporaryRewrittenHlsPlaylistAsync(string playlistText, Uri playlistUri, CancellationToken cancellationToken)
    {
        if (!playlistText.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rewrittenPlaylist = RewriteHlsPlaylistWithAbsoluteUrls(playlistText, playlistUri);
        var tempPath = Path.Combine(Path.GetTempPath(), $"JasperMediaPlayer_HLS_{Guid.NewGuid():N}.m3u8");
        await File.WriteAllTextAsync(tempPath, rewrittenPlaylist, Encoding.UTF8, cancellationToken);
        return tempPath;
    }

    private static string RewriteHlsPlaylistWithAbsoluteUrls(string playlistText, Uri playlistUri)
    {
        var builder = new StringBuilder();
        var lines = playlistText.Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                builder.AppendLine();
                continue;
            }

            if (line.StartsWith('#'))
            {
                builder.AppendLine(RewriteHlsUriAttributes(line, playlistUri));
                continue;
            }

            builder.AppendLine(MakeAbsoluteUrl(line.Trim(), playlistUri));
        }

        return builder.ToString();
    }

    private static string RewriteHlsUriAttributes(string line, Uri playlistUri)
    {
        return HlsUriAttributeRegex.Replace(line, match =>
        {
            var uriValue = match.Groups["uri"].Value;
            var absolute = MakeAbsoluteUrl(uriValue, playlistUri);
            return $"URI=\"{absolute}\"";
        });
    }

    private static string MakeAbsoluteUrl(string value, Uri baseUri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        return new Uri(baseUri, value).AbsoluteUri;
    }

    private int PlayMedia(Media media, string statusText, string? temporaryHlsPath = null, bool isNetwork = false)
    {
        var oldTemporaryHlsPath = _temporaryHlsPath;
        var sessionId = ++_playbackSessionId;

        try
        {
            _playbackFailedDuringAttempt = false;
            _currentSourceIsNetwork = isNetwork;
            _activeBackend = PlaybackBackend.Vlc;
            StopNativeStream(clearSource: true);
            _mediaPlayer.Stop();

            _currentMedia?.Dispose();
            _currentMedia = media;
            _temporaryHlsPath = temporaryHlsPath;

            TryDeleteTemporaryFile(oldTemporaryHlsPath);

            EmptyState.Visibility = Visibility.Collapsed;
            NativeStreamView.Visibility = Visibility.Collapsed;
            VideoView.Visibility = Visibility.Visible;
            SetStatus(statusText);

            var started = _mediaPlayer.Play(_currentMedia);
            if (!started)
            {
                _playbackFailedDuringAttempt = true;
                SetStatus("VLC refused to start this media. Trying another stream mode if available...");
            }
        }
        catch (Exception ex)
        {
            _playbackFailedDuringAttempt = true;
            media.Dispose();
            TryDeleteTemporaryFile(temporaryHlsPath);
            SetStatus($"Could not start playback: {ex.Message}");
        }

        return sessionId;
    }

    private async Task<bool> WaitForNetworkAttemptAsync(int sessionId, int seconds, bool requiresProgress)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(seconds);
        var sawPlayingState = false;

        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(500);

            if (sessionId != _playbackSessionId)
            {
                return true;
            }

            if (_playbackFailedDuringAttempt)
            {
                return false;
            }

            var state = _mediaPlayer.State;

            if (state is VLCState.Stopped or VLCState.Ended or VLCState.Error)
            {
                return false;
            }

            if (state is VLCState.Playing or VLCState.Paused)
            {
                sawPlayingState = true;

                // This is the important anti-black-screen test.
                // Some HLS attempts fire the Playing event but never expose a duration or advancing time.
                if (!requiresProgress || _mediaPlayer.Length > 0 || _mediaPlayer.Time > 0)
                {
                    return true;
                }
            }
        }

        if (sawPlayingState)
        {
            SetStatus("That stream mode started but showed no video progress. Trying the next mode...");
        }

        return false;
    }

    private void Timer_Tick(object? sender, object e)
    {
        if (_activeBackend == PlaybackBackend.Native && _nativeHasMedia)
        {
            UpdateNativeTimeUi();
            return;
        }

        if (_mediaPlayer.Length <= 0)
        {
            var state = _mediaPlayer.State;
            if (_mediaPlayer.IsPlaying || state is VLCState.Opening or VLCState.Buffering or VLCState.Playing)
            {
                TimeText.Text = $"{FormatTime(_mediaPlayer.Time)} / LIVE";
            }
            else
            {
                TimeText.Text = "00:00 / 00:00";
            }

            return;
        }

        var shouldHoldUserSeekPosition = _isDraggingSeek || DateTimeOffset.Now < _dontSnapSeekBackUntil;

        if (!shouldHoldUserSeekPosition)
        {
            _isUpdatingSeekFromPlayer = true;
            SeekSlider.Value = Math.Clamp((_mediaPlayer.Time / (double)_mediaPlayer.Length) * 1000.0, 0, 1000);
            _isUpdatingSeekFromPlayer = false;
        }

        var shownTime = shouldHoldUserSeekPosition
            ? (long)(_mediaPlayer.Length * (SeekSlider.Value / 1000.0))
            : _mediaPlayer.Time;

        TimeText.Text = $"{FormatTime(shownTime)} / {FormatTime(_mediaPlayer.Length)}";
    }

    private void UpdateNativeTimeUi()
    {
        TimeSpan position;
        TimeSpan duration;

        try
        {
            var session = _nativeStreamPlayer.PlaybackSession;
            position = session.Position;
            duration = session.NaturalDuration;
        }
        catch
        {
            TimeText.Text = "00:00 / LIVE";
            return;
        }

        var hasDuration = duration > TimeSpan.Zero && duration.TotalMilliseconds > 0;

        var shouldHoldUserSeekPosition = _isDraggingSeek || DateTimeOffset.Now < _dontSnapSeekBackUntil;

        if (hasDuration && !shouldHoldUserSeekPosition)
        {
            _isUpdatingSeekFromPlayer = true;
            SeekSlider.Value = Math.Clamp((position.TotalMilliseconds / duration.TotalMilliseconds) * 1000.0, 0, 1000);
            _isUpdatingSeekFromPlayer = false;
        }

        if (!hasDuration)
        {
            TimeText.Text = $"{FormatTime((long)position.TotalMilliseconds)} / LIVE";
            return;
        }

        var shownTime = shouldHoldUserSeekPosition
            ? TimeSpan.FromMilliseconds(duration.TotalMilliseconds * (SeekSlider.Value / 1000.0))
            : position;

        TimeText.Text = $"{FormatTime((long)shownTime.TotalMilliseconds)} / {FormatTime((long)duration.TotalMilliseconds)}";
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSeek = true;
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SeekToSlider();
        _isDraggingSeek = false;
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_activeBackend == PlaybackBackend.Native)
        {
            if (_isUpdatingSeekFromPlayer || !_nativeHasMedia)
            {
                return;
            }

            SeekToNativeSliderPosition();
            return;
        }

        if (_isUpdatingSeekFromPlayer || _mediaPlayer is null || _mediaPlayer.Media is null || _mediaPlayer.Length <= 0)
        {
            return;
        }

        SeekToSlider();
    }

    private void SeekToSlider()
    {
        if (_activeBackend == PlaybackBackend.Native)
        {
            SeekToNativeSliderPosition();
            return;
        }

        if (_mediaPlayer.Media is null || _mediaPlayer.Length <= 0)
        {
            return;
        }

        _dontSnapSeekBackUntil = DateTimeOffset.Now.AddMilliseconds(900);
        _mediaPlayer.Position = Math.Clamp((float)(SeekSlider.Value / 1000.0), 0f, 1f);

        var previewTime = (long)(_mediaPlayer.Length * (SeekSlider.Value / 1000.0));
        TimeText.Text = $"{FormatTime(previewTime)} / {FormatTime(_mediaPlayer.Length)}";
    }

    private void SeekToNativeSliderPosition()
    {
        if (!_nativeHasMedia)
        {
            return;
        }

        Windows.Media.Playback.MediaPlaybackSession session;
        TimeSpan duration;

        try
        {
            session = _nativeStreamPlayer.PlaybackSession;
            duration = session.NaturalDuration;
        }
        catch
        {
            return;
        }

        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds <= 0)
        {
            return;
        }

        _dontSnapSeekBackUntil = DateTimeOffset.Now.AddMilliseconds(900);
        var target = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * (SeekSlider.Value / 1000.0));

        try
        {
            session.Position = target;
        }
        catch
        {
            return;
        }

        TimeText.Text = $"{FormatTime((long)target.TotalMilliseconds)} / {FormatTime((long)duration.TotalMilliseconds)}";
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Volume = (int)e.NewValue;
        }

        if (_nativeStreamPlayer is not null)
        {
            _nativeStreamPlayer.Volume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        }
    }

    private void UpdatePlayingUi()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        PlayPauseIcon.Glyph = "\uE769";
        PlayPauseText.Text = "Pause";
        SetStatus("Playing.");
    }

    private void UpdatePausedUi()
    {
        PlayPauseIcon.Glyph = "\uE768";
        PlayPauseText.Text = "Play";
    }

    private void UpdateStoppedUi()
    {
        PlayPauseIcon.Glyph = "\uE768";
        PlayPauseText.Text = "Play";
        _isUpdatingSeekFromPlayer = true;
        SeekSlider.Value = 0;
        _isUpdatingSeekFromPlayer = false;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "00:00";
        }

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp cleanup should never crash the player.
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _currentSourceIsNetwork = false;
        StopNativeStream(clearSource: true);
        NativeStreamView.SetMediaPlayer(null);
        _nativeStreamPlayer.Dispose();
        _mediaPlayer.Stop();
        VideoView.MediaPlayer = null;
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _httpClient.Dispose();
        TryDeleteTemporaryFile(_temporaryHlsPath);
    }

    private sealed record PlaybackCandidate(Media Media, string StatusText, string SuccessText, string? TemporaryPlaylistPath, int WaitSeconds, bool RequiresProgress);
    private sealed record HlsVariant(Uri Uri, int Bandwidth, int Width, int Height, string Codecs);
    private enum PlaybackBackend
    {
        None,
        Vlc,
        Native
    }
}
