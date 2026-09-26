namespace Xakpc.SQLiteMCPSidecar.Tests.Fixtures;

/// <summary>
/// Builds the sample database that <c>scripts/dev-sidecar.ps1</c> serves. It is a test so that the
/// dev script and the test suite share one seeding implementation, with no second project and no
/// <c>sqlite3</c> prerequisite.
/// </summary>
public sealed class DevDatabaseTests
{
    [Fact]
    public void Create()
    {
        var target = Environment.GetEnvironmentVariable("SIDECAR_DEV_DB");
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip("SIDECAR_DEV_DB is not set. scripts/dev-sidecar.ps1 sets it.");
        }

        SampleDatabase.CreateAt(target!);

        Assert.True(File.Exists(target));
    }
}
