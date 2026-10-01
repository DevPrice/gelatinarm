using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    /// <summary>
    ///     Shows the subtitles the server delivers as files (text formats) in the player, so choosing,
    ///     switching or hiding one needs no new stream. The server burns the others into the video.
    /// </summary>
    internal sealed class TextSubtitlePresenter
    {
        // TimedTextSource has parsed SubRip since the first Windows 10 release. The subtitle endpoint
        // converts every text format to it, ASS and SSA included, which loses their styling.
        private const string DeliveryFormat = "srt";

        private readonly ILogger _logger;
        private readonly IAuthenticationService _authService;
        private readonly object _lock = new object();
        private readonly HashSet<int> _loadedStreams = new HashSet<int>();
        private readonly Dictionary<int, List<TimedMetadataTrack>> _tracksByStream = new Dictionary<int, List<TimedMetadataTrack>>();

        private MediaSource _mediaSource;
        private MediaSourceInfo _sourceInfo;
        private Guid _itemId;
        private MediaPlaybackItem _playbackItem;
        private int _shownStreamIndex = -1;

        public TextSubtitlePresenter(ILogger logger, IAuthenticationService authService)
        {
            _logger = logger;
            _authService = authService;
        }

        public static bool IsDeliveredAsFile(MediaStream stream)
        {
            return stream?.Type == MediaStream_Type.Subtitle
                   && stream.DeliveryMethod == MediaStream_DeliveryMethod.External;
        }

        /// <summary>
        ///     The item for a newly opened stream, showing subtitle <paramref name="streamIndex" /> when the
        ///     server delivers it as a file. Replaces the previous stream's subtitles.
        /// </summary>
        public MediaPlaybackItem CreatePlaybackItem(MediaSource mediaSource, MediaSourceInfo sourceInfo, Guid itemId,
            int streamIndex)
        {
            Detach();
            lock (_lock)
            {
                _mediaSource = mediaSource;
                _sourceInfo = sourceInfo;
                _itemId = itemId;
            }

            // Before the item exists: Microsoft's documented order for external timed text
            TrySelect(streamIndex);

            var playbackItem = new MediaPlaybackItem(mediaSource);
            playbackItem.TimedMetadataTracksChanged += OnTimedMetadataTracksChanged;
            lock (_lock)
            {
                _playbackItem = playbackItem;
            }

            ApplyPresentationModes();
            return playbackItem;
        }

        /// <summary>
        ///     Shows subtitle <paramref name="streamIndex" />, or none for -1, in the open stream. False
        ///     when the server does not deliver that stream as a file: only a stream that burns it in shows it.
        /// </summary>
        public bool TrySelect(int streamIndex)
        {
            MediaStream streamToLoad = null;
            lock (_lock)
            {
                if (_mediaSource == null)
                {
                    return false;
                }

                if (streamIndex >= 0)
                {
                    var stream = _sourceInfo?.MediaStreams?.FirstOrDefault(s => s.Index == streamIndex);
                    if (!IsDeliveredAsFile(stream))
                    {
                        return false;
                    }

                    if (_loadedStreams.Add(streamIndex))
                    {
                        streamToLoad = stream;
                    }
                }

                _shownStreamIndex = streamIndex;
            }

            if (streamToLoad != null)
            {
                Load(streamToLoad);
            }

            ApplyPresentationModes();
            return true;
        }

        /// <summary>
        ///     Forgets the stream's subtitles; its MediaSource, which holds them, is the caller's to dispose
        /// </summary>
        public void Detach()
        {
            MediaPlaybackItem playbackItem;
            lock (_lock)
            {
                playbackItem = _playbackItem;
                _playbackItem = null;
                _mediaSource = null;
                _sourceInfo = null;
                _loadedStreams.Clear();
                _tracksByStream.Clear();
                _shownStreamIndex = -1;
            }

            if (playbackItem != null)
            {
                playbackItem.TimedMetadataTracksChanged -= OnTimedMetadataTracksChanged;
            }
        }

        // Fetched when first shown, not at open: an embedded track is extracted by reading the whole
        // file on the server, which for a large remux takes long enough to matter per track.
        private void Load(MediaStream stream)
        {
            MediaSource mediaSource;
            string url;
            lock (_lock)
            {
                mediaSource = _mediaSource;
                if (mediaSource == null)
                {
                    return;
                }

                url = BuildDeliveryUrl(stream);
            }

            var streamIndex = stream.Index ?? -1;
            _logger.LogInformation("Loading subtitle stream {StreamIndex} ({Codec}) as {DeliveryFormat}: {Url}",
                streamIndex, stream.Codec, DeliveryFormat, UrlHelper.RedactApiKey(url));
            try
            {
                var timedTextSource = TimedTextSource.CreateFromUri(new Uri(url));
                timedTextSource.Resolved += (sender, args) => OnResolved(mediaSource, streamIndex, args);
                mediaSource.ExternalTimedTextSources.Add(timedTextSource);
            }
            catch (Exception ex)
            {
                // A subtitle that cannot load must not stop the video
                ReportError(ex, nameof(Load));
            }
        }

        // Built here rather than taken from the stream's DeliveryUrl, which names the stream's own
        // format: the server converts nothing out of ASS or SSA when it picks a subtitle profile, but
        // its subtitle endpoint does. Start 0: a direct play and the server's HLS stream both keep the
        // file's timestamps. The token goes in the query because TimedTextSource sends no headers.
        private string BuildDeliveryUrl(MediaStream stream)
        {
            var path = $"/Videos/{_itemId:N}/{Uri.EscapeDataString(_sourceInfo.Id ?? string.Empty)}" +
                       $"/Subtitles/{stream.Index}/0/Stream.{DeliveryFormat}";
            return UrlHelper.AppendApiKey(UrlHelper.ResolveServerUrl(_authService.ServerUrl, path),
                _authService.AccessToken);
        }

        private void OnResolved(MediaSource owner, int streamIndex, TimedTextSourceResolveResultEventArgs args)
        {
            try
            {
                if (args.Error != null)
                {
                    _logger.LogWarning("Subtitle stream {StreamIndex} did not load: {ErrorCode} ({HResult:X8})",
                        streamIndex, args.Error.ErrorCode, args.Error.ExtendedError?.HResult ?? 0);
                    return;
                }

                lock (_lock)
                {
                    if (owner != _mediaSource)
                    {
                        return;
                    }

                    _tracksByStream[streamIndex] = args.Tracks.ToList();
                }

                _logger.LogInformation("Subtitle stream {StreamIndex} loaded: {TrackCount} track(s)",
                    streamIndex, args.Tracks.Count);
                ApplyPresentationModes();
            }
            catch (Exception ex)
            {
                ReportError(ex, nameof(OnResolved));
            }
        }

        private void OnTimedMetadataTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
        {
            try
            {
                ApplyPresentationModes();
            }
            catch (Exception ex)
            {
                ReportError(ex, nameof(OnTimedMetadataTracksChanged));
            }
        }

        // Runs on the source's Resolved and on the item's track list changing, since either can come
        // second. Tracks are matched by reference: the projection hands back one wrapper per track.
        // Every other subtitle track is disabled, including any the file carries in-band.
        private void ApplyPresentationModes()
        {
            MediaPlaybackItem playbackItem;
            List<TimedMetadataTrack> shownTracks;
            List<TimedMetadataTrack> loadedTracks;
            lock (_lock)
            {
                playbackItem = _playbackItem;
                if (playbackItem == null)
                {
                    return;
                }

                _tracksByStream.TryGetValue(_shownStreamIndex, out shownTracks);
                loadedTracks = _tracksByStream.Values.SelectMany(tracks => tracks).ToList();
            }

            var itemTracks = playbackItem.TimedMetadataTracks;
            for (var i = 0; i < itemTracks.Count; i++)
            {
                var track = itemTracks[i];
                TimedMetadataTrackPresentationMode mode;
                if (shownTracks?.Contains(track) == true)
                {
                    mode = TimedMetadataTrackPresentationMode.PlatformPresented;
                }
                else if (loadedTracks.Contains(track)
                         || track.TimedMetadataKind == TimedMetadataKind.Subtitle
                         || track.TimedMetadataKind == TimedMetadataKind.Caption)
                {
                    mode = TimedMetadataTrackPresentationMode.Disabled;
                }
                else
                {
                    continue;
                }

                if (itemTracks.GetPresentationMode((uint)i) != mode)
                {
                    itemTracks.SetPresentationMode((uint)i, mode);
                }
            }
        }

        private static void ReportError(Exception ex, string operation)
        {
            ServiceLocator.GetRequiredService<IErrorHandlingService>().HandleError(ex,
                new ErrorContext(nameof(TextSubtitlePresenter), operation, ErrorCategory.Media, ErrorSeverity.Warning));
        }
    }
}
