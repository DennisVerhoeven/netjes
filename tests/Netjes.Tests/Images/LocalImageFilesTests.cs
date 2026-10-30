using Netjes.Images;

namespace Netjes.Tests.Images;

public class LocalImageFilesTests
{
    /// <summary>A directory holding <c>0.png</c> to <c>{Capacity}.png</c>: one more image than the spellings remembered.</summary>
    private sealed class ManyImages : LocalImageFiles
    {
        private static readonly string[] Names = [.. Enumerable.Range(0, Capacity + 1).Select(i => $"{i}.png")];

        public override IEnumerable<string> NamesIn(string directory) => Names;
    }

    [Fact]
    public void RemembersAtMostCapacitySpellings()
    {
        // Only files spelled as stored are remembered, but public/ can hold any number of them.
        var files = new ManyImages();

        for (var i = 0; i <= LocalImageFiles.Capacity; i++)
        {
            Assert.True(files.IsStoredSpelling("/public", $"/{i}.png"));
        }

        Assert.InRange(files.Remembered, 1, LocalImageFiles.Capacity);
    }
}
