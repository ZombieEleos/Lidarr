using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class RescanArtistCommand : Command
    {
        public int? ArtistId { get; set; }
        public string Path { get; set; }

        public override bool SendUpdatesToClient => true;

        public RescanArtistCommand()
        {
        }

        public RescanArtistCommand(int artistId)
        {
            ArtistId = artistId;
        }

        public RescanArtistCommand(int artistId, string path)
        {
            ArtistId = artistId;
            Path = path;
        }

    }
}
