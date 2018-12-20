using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Identification
{
    public interface IIdentificationService
    {
        List<LocalAlbumRelease> Identify(List<LocalTrack> localTracks, Artist artist, Album album, AlbumRelease release);
    }

    public class IdentificationService : IIdentificationService
    {
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IReleaseService _releaseService;
        private readonly ITrackService _trackService;
        private readonly ITrackGroupingService _trackGroupingService;
        private readonly IFingerprintingService _fingerprintingService;
        private readonly IAugmentingService _augmentingService;
        private readonly Logger _logger;

        public IdentificationService(IArtistService artistService,
                                     IAlbumService albumService,
                                     IReleaseService releaseService,
                                     ITrackService trackService,
                                     ITrackGroupingService trackGroupingService,
                                     IFingerprintingService fingerprintingService,
                                     IAugmentingService augmentingService,
                                     Logger logger)
        {
            _artistService = artistService;
            _albumService = albumService;
            _releaseService = releaseService;
            _trackService = trackService;
            _trackGroupingService = trackGroupingService;
            _fingerprintingService = fingerprintingService;
            _augmentingService = augmentingService;
            _logger = logger;
        }

        private List<IsoCountry> preferredCountries = new List<string> {
            "United Kingdom",
            "United States",
            "Europe",
            "[Worldwide]"
        }.Select(x => IsoCountries.Find(x)).ToList();

        private readonly List<string> VariousArtistNames = new List<string> { "various artists", "various", "va", "unknown" };
        private readonly List<string> VariousArtistIds = new List<string> { "89ad4ac3-39f7-470e-963a-56509c546377" };

        public List<LocalAlbumRelease> Identify(List<LocalTrack> localTracks, Artist artist, Album album, AlbumRelease release)
        {
            // 1 group localTracks so that we think they represent a single release
            // 2 get candidates given specified artist, album and release
            // 3 find best candidate
            // 4 If best candidate worse than threshold, try fingerprinting

            _logger.Debug("Starting track indentification");
            _logger.Debug("Specified artist {0}, album {1}, release {2}", artist.NullSafe(), album.NullSafe(), release.NullSafe());
            _logger.Trace("Processing files:\n{0}", string.Join("\n", localTracks.Select(x => x.Path)));

            var releases = _trackGroupingService.GroupTracks(localTracks);
            
            foreach (var localRelease in releases)
            {
                _augmentingService.Augment(localRelease);
                IdentifyRelease(localRelease, artist, album, release, false);
            }

            return releases;
        }

        private void IdentifyRelease(LocalAlbumRelease localAlbumRelease, Artist artist, Album album, AlbumRelease release, bool forceFingerprint)
        {
            var candidateReleases = GetCandidates(localAlbumRelease, artist, album, release, forceFingerprint);
            GetBestRelease(localAlbumRelease, candidateReleases);

            // if result isn't great and we haven't fingerprinted, try that
            // Don't fingerprint if Album or Release has been overridden
            if (localAlbumRelease.Distance.NormalizedDistance > 0.15
                && !forceFingerprint
                && album == null
                && release == null
                && localAlbumRelease.LocalTracks[0].AcoustIdResults == null)
            {
                IdentifyRelease(localAlbumRelease, artist, album, release, true);
                return;
            }

            localAlbumRelease.PopulateMatch();
        }

        private List<AlbumRelease> GetCandidates(LocalAlbumRelease localAlbumRelease, Artist artist, Album album, AlbumRelease release, bool forceFingerprint)
        {
            // Generally artist, album and release are null.  But if they're not then limit candidates appropriately.
            // We've tried to make sure that tracks are all for a single release.
            
            var candidateReleases = new List<AlbumRelease>();

            // if we have a release ID, use that
            var releaseIds = localAlbumRelease.LocalTracks.Select(x => x.FileTrackInfo.ReleaseMBId).Distinct().ToList();
            if (releaseIds.Count == 1 && releaseIds[0].IsNotNullOrWhiteSpace())
            {
                _logger.Debug("Selecting release from consensus ForeignReleaseId [{0}]", releaseIds[0]);
                return _releaseService.GetReleasesByForeignReleaseId(releaseIds);
            }

            if (!forceFingerprint)
            {
                if (release != null)
                {
                    _logger.Debug("Release {0} [{1} tracks] was forced", release, release.TrackCount);
                    candidateReleases = new List<AlbumRelease> { release };
                }
                else if (album != null)
                {
                    candidateReleases = GetCandidatesByAlbum(localAlbumRelease, album);
                }
                else if (artist != null)
                {
                    candidateReleases = GetCandidatesByArtist(localAlbumRelease, artist);
                }
                else
                {
                    candidateReleases = GetCandidates(localAlbumRelease);
                }
            }

            // if we haven't got any candidates then try fingerprinting
            if (candidateReleases.Count == 0)
            {
                _logger.Debug("No candidates found, fingerprinting");
                _fingerprintingService.Lookup(localAlbumRelease.LocalTracks, 0.5);
                candidateReleases = GetCandidatesByFingerprint(localAlbumRelease);
            }

            return candidateReleases;
        }

        private List<AlbumRelease> GetCandidatesByAlbum(LocalAlbumRelease localAlbumRelease, Album album)
        {
            // sort candidate releases by closest track count so that we stand a chance of
            // getting a perfect match early on
            return _releaseService.GetReleasesByAlbum(album.Id)
                .Where(x => Math.Abs(localAlbumRelease.TrackCount - x.TrackCount) <= 5)
                .OrderBy(x => Math.Abs(localAlbumRelease.TrackCount - x.TrackCount))
                .ToList();
        }

        private List<AlbumRelease> GetCandidatesByArtist(LocalAlbumRelease localAlbumRelease, Artist artist)
        {
            _logger.Trace("Getting candidates for {0}", artist);
            var candidateReleases = new List<AlbumRelease>();
            
            var albumTag = MostCommon(localAlbumRelease.LocalTracks.Select(x => x.FileTrackInfo.AlbumTitle)) ?? "";
            if (albumTag.IsNotNullOrWhiteSpace())
            {
                var possibleAlbums = _albumService.GetCandidates(artist.ArtistMetadataId, albumTag);
                foreach (var album in possibleAlbums)
                {
                    candidateReleases.AddRange(GetCandidatesByAlbum(localAlbumRelease, album));
                }
            }

            return candidateReleases;
        }

        private List<AlbumRelease> GetCandidates(LocalAlbumRelease localAlbumRelease)
        {
            // most general version, nothing has been specified.
            // get all plausible artists, then all plausible albums, then get releases for each of these.

            // check if it looks like VA.
            if (TrackGroupingService.IsVariousArtists(localAlbumRelease.LocalTracks))
            {
                throw new NotImplementedException("Various artists not supported");
            }

            var candidateReleases = new List<AlbumRelease>();
            
            var artistTag = MostCommon(localAlbumRelease.LocalTracks.Select(x => x.FileTrackInfo.ArtistTitle)) ?? "";
            if (artistTag.IsNotNullOrWhiteSpace())
            {
                var possibleArtists = _artistService.GetCandidates(artistTag);
                foreach (var artist in possibleArtists)
                {
                    candidateReleases.AddRange(GetCandidatesByArtist(localAlbumRelease, artist));
                }
            }

            return candidateReleases;
        }

        private List<AlbumRelease> GetCandidatesByFingerprint(LocalAlbumRelease localAlbumRelease)
        {
            var recordingIds = localAlbumRelease.LocalTracks.SelectMany(x => x.AcoustIdResults).ToList();
            var allReleases = _releaseService.GetReleasesByRecordingIds(recordingIds);

            return allReleases.Select(x => new {
                    Release = x,
                    TrackCount = x.TrackCount,
                    CommonProportion = x.Tracks.Value.Select(y => y.ForeignRecordingId).Intersect(recordingIds).Count() / localAlbumRelease.TrackCount
                })
                .Where(x => x.CommonProportion > 0.6)
                .ToList()
                .OrderBy(x => Math.Abs(x.TrackCount - localAlbumRelease.TrackCount))
                .ThenByDescending(x => x.CommonProportion)
                .Select(x => x.Release)
                .Take(10)
                .ToList();
        }

        private T MostCommon<T>(IEnumerable<T> items)
        {
            return items.GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key;
        }

        private void GetBestRelease(LocalAlbumRelease localAlbumRelease, List<AlbumRelease> candidateReleases)
        {
            _logger.Debug("Matching {0} track files against {1} candidates", localAlbumRelease.TrackCount, candidateReleases.Count);
            _logger.Trace("Processing files:\n{0}", string.Join("\n", localAlbumRelease.LocalTracks.Select(x => x.Path)));

            foreach (var release in candidateReleases)
            {
                _logger.Debug("Trying Release {0} [{1}, {2} tracks]", release, release.Title, release.TrackCount);
                var mbTracks = _trackService.GetTracksByRelease(release.Id);
                var mapping = MapReleaseTracks(localAlbumRelease.LocalTracks, mbTracks);
                var distance = AlbumReleaseDistance(localAlbumRelease.LocalTracks, release, mapping);
                _logger.Debug("Release {0} [{1} tracks] has distance {2} vs best distance {3}",
                              release, release.TrackCount, distance.NormalizedDistance, localAlbumRelease.Distance.NormalizedDistance);
                if (distance.NormalizedDistance < localAlbumRelease.Distance.NormalizedDistance)
                {
                    localAlbumRelease.Distance = distance;
                    localAlbumRelease.AlbumRelease = release;
                    localAlbumRelease.TrackMapping = mapping;
                    if (localAlbumRelease.Distance.NormalizedDistance == 0.0)
                    {
                        break;
                    }
                }
            }

            _logger.Debug("Best release: {0} Distance {1}", localAlbumRelease.AlbumRelease, localAlbumRelease.Distance.NormalizedDistance);
        }

        private TrackMapping MapReleaseTracks(List<LocalTrack> localTracks, List<Track> mbTracks)
        {
            var costs = new double[localTracks.Count, mbTracks.Count];

            for (int row = 0; row < localTracks.Count; row++)
            {
                for (int col = 0; col < mbTracks.Count; col++)
                {
                    costs[row, col] = TrackDistance(localTracks[row], mbTracks[col]).NormalizedDistance;
                    // _logger.Trace("Distance between {0} and {1}: {2}", localTracks[row], mbTracks[col], costs[row, col]);
                }
            }

            var m = new Munkres(costs);
            m.Run();

            var result = new TrackMapping();
            foreach (var pair in m.Solution)
            {
                result.Mapping.Add(localTracks[pair.Item1], mbTracks[pair.Item2]);
                _logger.Trace("Mapped {0} to {1}, dist: {2}", localTracks[pair.Item1], mbTracks[pair.Item2], costs[pair.Item1, pair.Item2]);
            }
            result.LocalExtra = localTracks.Except(result.Mapping.Keys).ToList();
            result.MBExtra = mbTracks.Except(result.Mapping.Values).ToList();
            
            return result;
        }

        private bool TrackIndexIncorrect(LocalTrack localTrack, Track mbTrack)
        {
            // needs updating to deal with total track numbering, not per disc
            return localTrack.FileTrackInfo.TrackNumbers.First() != mbTrack.AbsoluteTrackNumber;
        }

        private Distance TrackDistance(LocalTrack localTrack, Track mbTrack, bool includeArtist = false)
        {
            var dist = new Distance();

            var localLength = localTrack.FileTrackInfo.Duration.TotalSeconds;
            var mbLength = mbTrack.Duration / 1000;
            var diff = Math.Abs(localLength - mbLength) - 10;

            if (mbLength > 0)
            {
                dist.AddRatio("track_length", diff, 30);
                // _logger.Trace("track_length: {0} vs {1}, diff: {2} grace: 30; {3}",
                //               localLength, mbLength, diff, dist.NormalizedDistance);
            }

            dist.AddString("track_title", localTrack.FileTrackInfo.Title ?? "", mbTrack.Title);

            if (includeArtist && localTrack.FileTrackInfo.ArtistTitle.IsNotNullOrWhiteSpace()
                && !VariousArtistNames.Any(x => x.Equals(localTrack.FileTrackInfo.ArtistTitle, StringComparison.InvariantCultureIgnoreCase)))
            {
                dist.AddString("track_artist", localTrack.FileTrackInfo.ArtistTitle, mbTrack.ArtistMetadata.Value.Name);
            }

            if (localTrack.FileTrackInfo.TrackNumbers.First() > 0 && mbTrack.AbsoluteTrackNumber > 0)
            {
                dist.AddExpr("track_index", () => TrackIndexIncorrect(localTrack, mbTrack));
                // _logger.Trace("track_index: {0} vs {1}; {2}", localTrack.FileTrackInfo.TrackNumbers.First(), mbTrack.AbsoluteTrackNumber, dist.NormalizedDistance);
            }

            var recordingId = localTrack.FileTrackInfo.RecordingMBId;
            if (recordingId.IsNotNullOrWhiteSpace())
            {
                dist.AddExpr("recording_id", () => localTrack.FileTrackInfo.RecordingMBId != mbTrack.ForeignRecordingId);
                // _logger.Trace("recording_id: {0} vs {1}; {2}", localTrack.FileTrackInfo.RecordingMBId, mbTrack.ForeignRecordingId, dist.NormalizedDistance);
            }

            // for fingerprinted files
            if (localTrack.AcoustIdResults != null)
            {
                dist.AddExpr("recording_id", () => !localTrack.AcoustIdResults.Contains(mbTrack.ForeignRecordingId));
                _logger.Trace("fingerprinting: {0} vs {1}; {2}", string.Join(", ", localTrack.AcoustIdResults), mbTrack.ForeignRecordingId, dist.NormalizedDistance);
            }

            return dist;
        }

        private Distance AlbumReleaseDistance(List<LocalTrack> localTracks, AlbumRelease release, TrackMapping mapping)
        {
            var dist = new Distance();

            if (!VariousArtistIds.Contains(release.Album.Value.ArtistMetadata.Value.ForeignArtistId))
            {
                var artist = MostCommon(localTracks.Select(x => x.FileTrackInfo.ArtistTitle)) ?? "";
                dist.AddString("artist", artist, release.Album.Value.ArtistMetadata.Value.Name);
                _logger.Trace("artist: {0} vs {1}; {2}", artist, release.Album.Value.ArtistMetadata.Value.Name, dist.NormalizedDistance);
            }

            var title = MostCommon(localTracks.Select(x => x.FileTrackInfo.AlbumTitle)) ?? "";
            dist.AddString("album", title, release.Title.IsNotNullOrWhiteSpace() ? release.Title : release.Album.Value.Title);
            _logger.Trace("album: {0} vs {1}; {2}", title, release.Title, dist.NormalizedDistance);

            // Number of discs, either as tagged or the max disc number seen
            var discCount = MostCommon(localTracks.Select(x => x.FileTrackInfo.DiscCount));
            discCount = discCount != 0 ? discCount : localTracks.Max(x => x.FileTrackInfo.DiscNumber);
            if (discCount > 0)
            {
                dist.AddNumber("mediums", discCount, release.Media.Count);
                _logger.Trace("mediums: {0} vs {1}; {2}", discCount, release.Media.Count, dist.NormalizedDistance);
            }

            // Year
            var localYear = MostCommon(localTracks.Select(x => x.FileTrackInfo.Year));
            var mbYear = release.Album.Value.ReleaseDate.Value.Year;
            if (localYear == mbYear)
            {
                dist.Add("year", 0.0);
            }
            else if (release.Album.Value.ReleaseDate.HasValue)
            {
                var diff = Math.Abs(localYear - mbYear);
                var diff_max = Math.Abs(DateTime.Now.Year - mbYear);
                dist.AddRatio("year", diff, diff_max);
            }
            else
            {
                // full penalty when there is no year
                dist.Add("year", 1.0);
            }

            var country = MostCommon(localTracks.Select(x => x.FileTrackInfo.Country));
            if (release.Country.Count > 0)
            {
                if (preferredCountries.Count > 0)
                {
                    dist.AddPriority("country", release.Country, preferredCountries.Select(x => x.Name).ToList());
                    _logger.Trace("country priority: {0} vs {1}; {2}", string.Join(", ", preferredCountries.Select(x => x.Name)), string.Join(", ", release.Country), dist.NormalizedDistance);
                }
                else if (country != null)
                {
                    dist.AddEquality("country", country.Name, release.Country);
                    _logger.Trace("country: {0} vs {1}; {2}", country, string.Join(", ", release.Country), dist.NormalizedDistance);
                }
            }

            var label = MostCommon(localTracks.Select(x => x.FileTrackInfo.Label));
            if (label.IsNotNullOrWhiteSpace())
            {
                dist.AddEquality("label", label, release.Label);
            }

            var disambig = MostCommon(localTracks.Select(x => x.FileTrackInfo.Disambiguation));
            if (disambig.IsNotNullOrWhiteSpace())
            {
                dist.AddString("albumdisambig", disambig, release.Disambiguation);
            }
            
            var mbAlbumId = MostCommon(localTracks.Select(x => x.FileTrackInfo.ReleaseMBId));
            if (mbAlbumId.IsNotNullOrWhiteSpace())
            {
                dist.AddEquality("album_id", mbAlbumId, new List<string> { release.ForeignReleaseId });
                _logger.Trace("album_id: {0} vs {1}; {2}", mbAlbumId, release.ForeignReleaseId, dist.NormalizedDistance);
            }

            // tracks
            foreach (var pair in mapping.Mapping)
            {
                dist.Add("tracks", TrackDistance(pair.Key, pair.Value).NormalizedDistance);
            }
            _logger.Trace("after trackMapping: {0}", dist.NormalizedDistance);

            // missing tracks
            foreach (var track in mapping.MBExtra)
            {
                dist.Add("missing_tracks", 1.0);
            }
            _logger.Trace("after missing tracks: {0}", dist.NormalizedDistance);

            // unmatched tracks
            foreach (var track in mapping.LocalExtra)
            {
                dist.Add("unmatched_tracks", 1.0);
            }
            _logger.Trace("after unmatched tracks: {0}", dist.NormalizedDistance);

            return dist;
        }
    }
}
