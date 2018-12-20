using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Identification
{
    public interface ITrackGroupingService
    {
        List<LocalAlbumRelease> GroupTracks(List<LocalTrack> localTracks);
    }

    public class TrackGroupingService : ITrackGroupingService
    {
        private readonly Logger _logger;

        private readonly List<string> multiDiscMarkers = new List<string> { @"dis[ck]", @"cd" };
        private readonly string multiDiscPatternFormat = @"^(?<root>.*%s[\W_]*)\d";

        public TrackGroupingService(Logger logger)
        {
            _logger = logger;
        }

        public List<LocalAlbumRelease> GroupTracks(List<LocalTrack> localTracks)
        {
            var releases = new List<LocalAlbumRelease>();

            // first attempt, assume grouped by folder
            var unprocessed = new List<LocalTrack>();
            foreach (var group in GroupTracksByDirectory(localTracks))
            {
                var tracks = group.ToList();
                if (LooksLikeSingleRelease(tracks))
                {
                    releases.Add(new LocalAlbumRelease(tracks));
                }
                else
                {
                    unprocessed.AddRange(tracks);
                }
            }

            // If anything didn't get grouped correctly, try grouping by Album (to pick up VA)
            var unprocessed2 = new List<LocalTrack>();
            foreach (var group in unprocessed.GroupBy(x => x.FileTrackInfo.AlbumTitle))
            {
                _logger.Debug("Falling back to grouping by album tag");
                var tracks = group.ToList();
                if (LooksLikeSingleRelease(tracks))
                {
                    releases.Add(new LocalAlbumRelease(tracks));
                }
                else
                {
                    unprocessed2.AddRange(tracks);
                }
            }

            // Finally fall back to grouping by Album/Artist pair
            foreach (var group in unprocessed2.GroupBy(x => new { x.FileTrackInfo.ArtistTitle, x.FileTrackInfo.AlbumTitle} ))
            {
                _logger.Debug("Falling back to grouping by album+artist tag");
                releases.Add(new LocalAlbumRelease(group.ToList()));
            }

            return releases;
        }

        public static bool LooksLikeSingleRelease(List<LocalTrack> tracks)
        {
            // returns true if we think all the tracks belong to a single release
            
            const double albumTagThreshold = 0.25;
            const double artistTagThreshold = 0.25;
            
            // check that any Album/Release MBID is unique
            if (tracks.Select(x => x.FileTrackInfo.AlbumMBId).Distinct().Where(x => x.IsNotNullOrWhiteSpace()).Count() > 1 ||
                tracks.Select(x => x.FileTrackInfo.ReleaseMBId).Distinct().Where(x => x.IsNotNullOrWhiteSpace()).Count() > 1)
            {
                return false;
            }
            
            // check that there's a common album tag.  
            var albumTags = tracks.Select(x => x.FileTrackInfo.AlbumTitle).GroupBy(x => x).OrderByDescending(x => x.Count());
            if (albumTags.Count() / tracks.Count > albumTagThreshold ||
                albumTags.First().Count() < 1 - albumTagThreshold)
            {
                return false;
            }

            // If not various artists, make sure artists are sensible
            if (!IsVariousArtists(tracks))
            {
                var artistTags = tracks.Select(x => x.FileTrackInfo.ArtistTitle).GroupBy(x => x).OrderByDescending(x => x.Count());
                if (artistTags.Count() / tracks.Count > artistTagThreshold ||
                    artistTags.First().Count() < 1 - artistTagThreshold)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsVariousArtists(List<LocalTrack> tracks)
        {
            // TODO identify if various artists
            return false;
        }

        private IEnumerable<List<LocalTrack>> GroupTracksByDirectory(List<LocalTrack> tracks)
        {
            // we want to check for layouts like:
            // xx/CD1/1.mp3
            // xx/CD2/1.mp3
            // or
            // yy Disc 1/1.mp3
            // yy Disc 2/1.mp3
            // and group them.

            // we only bother doing this for the immediate parent directory.
            var paths = tracks.Select(x => x.Path);
            var folders = paths.Select(x => Path.GetDirectoryName(x)).Distinct().ToList();
            folders.Sort();

            _logger.Trace("Folders:\n{0}", string.Join("\n", folders));

            Regex subdirRegex = null;
            var output = new List<LocalTrack>();
            foreach (var folder in folders)
            {
                if (subdirRegex != null)
                {
                    if (subdirRegex.IsMatch(folder))
                    {
                        // current folder continues match, so append output
                        output.AddRange(tracks.Where(x => x.Path.StartsWith(folder)));
                        continue;
                    }
                }

                // we have finished a multi disc match.  yield the previous output
                // and check current folder
                if (output.Count > 0)
                {
                    _logger.Trace("Yielding from 1:\n{0}", string.Join("\n", output));
                    yield return output;

                    output = new List<LocalTrack>();
                }

                // reset and put current folder into output
                subdirRegex = null;
                output.AddRange(tracks.Where(x => x.Path.StartsWith(folder)));

                // check if the start of another multi disc match
                foreach (var marker in multiDiscMarkers)
                {
                    // check if this is the first of a multi-disc set of folders
                    var pattern = multiDiscPatternFormat.Replace("%s", marker);
                    var multiStartRegex = new Regex(pattern, RegexOptions.IgnoreCase);

                    var match = multiStartRegex.Match(folder);
                    if (match.Success)
                    {
                        var subdirPattern = $"^{match.Groups["root"]}\\d+$";
                        subdirRegex = new Regex(subdirPattern, RegexOptions.IgnoreCase);
                        break;
                    }
                }

                if (subdirRegex == null)
                {
                    // not the start of a multi-disc match, yield
                    _logger.Trace("Yielding from 2:\n{0}", string.Join("\n", output));
                    yield return output;

                    // reset output
                    output = new List<LocalTrack>();
                }
            }

            // return the final stored output
            if (output.Count > 0)
            {
                _logger.Trace("Yielding final:\n{0}", string.Join("\n", output));
                yield return output;
            }
        }
    }
}
