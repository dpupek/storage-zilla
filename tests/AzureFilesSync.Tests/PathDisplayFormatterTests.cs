using AzureFilesSync.Core.Services;

namespace AzureFilesSync.Tests;

public sealed class PathDisplayFormatterTests
{
    [Theory]
    [InlineData(null, @"C:\Users\tester", @"C:\Users\tester")]
    [InlineData("", @"C:\Users\tester", @"C:\Users\tester")]
    [InlineData("   ", @"C:\Users\tester", @"C:\Users\tester")]
    [InlineData(@" C:\Data ", @"C:\Users\tester", @"C:\Data")]
    public void NormalizeLocalPath_UsesFallbackAndTrims(string? input, string fallback, string expected)
    {
        #region Arrange
        var formatter = new PathDisplayFormatter();
        #endregion

        #region Initial Assert
        Assert.NotNull(formatter);
        #endregion

        #region Act
        var result = formatter.NormalizeLocalPath(input, fallback);
        #endregion

        #region Assert
        Assert.Equal(expected, result);
        #endregion
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" / ", "")]
    [InlineData(" // ", "")]
    [InlineData(@" \ ", "")]
    [InlineData(@"foo\bar", "foo/bar")]
    [InlineData(" /foo/bar/ ", "foo/bar")]
    public void NormalizeRemotePathDisplay_NormalizesSeparatorsAndRoot(string? input, string expected)
    {
        #region Arrange
        var formatter = new PathDisplayFormatter();
        #endregion

        #region Initial Assert
        Assert.NotNull(formatter);
        #endregion

        #region Act
        var result = formatter.NormalizeRemotePathDisplay(input);
        #endregion

        #region Assert
        Assert.Equal(expected, result);
        #endregion
    }

    [Theory]
    [InlineData(null, "//")]
    [InlineData("", "//")]
    [InlineData("share/path", "//share/path")]
    [InlineData(" share/path ", "//share/path")]
    public void FormatRemotePathDisplay_UsesDoubleSlashRootPrefix(string? input, string expected)
    {
        #region Arrange
        var formatter = new PathDisplayFormatter();
        #endregion

        #region Initial Assert
        Assert.NotNull(formatter);
        #endregion

        #region Act
        var result = formatter.FormatRemotePathDisplay(input);
        #endregion

        #region Assert
        Assert.Equal(expected, result);
        #endregion
    }
}
