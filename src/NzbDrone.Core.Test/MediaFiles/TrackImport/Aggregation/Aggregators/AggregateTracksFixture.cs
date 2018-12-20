using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation.Aggregators;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Music;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.TrackImport.Aggregation.Aggregators
{
    [TestFixture]
    public class AugmentTracksFixture : CoreTest<AggregateTracks>
    {
        private Artist _artist;

        [SetUp]
        public void Setup()
        {
            _artist = Builder<Artist>.CreateNew().Build();

            var augmenters = new List<Mock<IAggregateLocalTrack>>
                             {
                                 new Mock<IAggregateLocalTrack>()
                             };

            Mocker.SetConstant(augmenters.Select(c => c.Object));
        }

        [Test]
        public void should_not_use_folder_when_it_contains_more_than_one_valid_audio_file()
        {
            var fileTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01E01");
            var folderTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01");
            var localTrack = new LocalTrack
            {
                FileTrackInfo = fileTrackInfo,
                FolderTrackInfo = folderTrackInfo,
                Path = @"C:\Test\Unsorted TV\Artist.Title.S01\Artist.Title.S01E01.mkv".AsOsAgnostic(),
                Artist = _artist
            };

            Subject.Aggregate(localTrack, true);

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetAlbum(_artist, fileTrackInfo), Times.Once());
        }

        [Test]
        [Ignore("Music doesn't have scene names")]
        public void should_not_use_folder_name_if_file_name_is_scene_name()
        {
            var fileTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01E01");
            var folderTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01E01");
            var localTrack = new LocalTrack
            {
                FileTrackInfo = fileTrackInfo,
                FolderTrackInfo = folderTrackInfo,
                Path = @"C:\Test\Unsorted TV\Artist.Title.S01E01\Artist.Title.S01E01.720p.HDTV-Sonarr.mkv".AsOsAgnostic(),
                Artist = _artist
            };

            Subject.Aggregate(localTrack, false);

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetTracks(_artist, null, fileTrackInfo), Times.Once());
        }

        [Test]
        [Ignore("Music should always use tag info")]
        public void should_use_folder_when_only_one_video_file()
        {
            var fileTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01E01");
            var folderTrackInfo = Parser.Parser.ParseMusicTitle("Artist.Title.S01E01");
            var localTrack = new LocalTrack
            {
                FileTrackInfo = fileTrackInfo,
                FolderTrackInfo = folderTrackInfo,
                Path = @"C:\Test\Unsorted TV\Artist.Title.S01E01\Artist.Title.S01E01.mkv".AsOsAgnostic(),
                Artist = _artist
            };

            Subject.Aggregate(localTrack, false);

            Mocker.GetMock<IParsingService>()
                .Verify(v => v.GetTracks(_artist, null, fileTrackInfo), Times.Once());
        }
    }
}
