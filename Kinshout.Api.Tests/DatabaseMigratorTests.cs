using Kinshout.Api.Data;

namespace Kinshout.Api.Tests;

public class DatabaseMigratorTests
{
    [Theory]
    [InlineData(2705)] // column already exists
    [InlineData(2714)] // object already exists
    [InlineData(1913)] // index already exists
    [InlineData(3701)] // dropped object already gone
    [InlineData(4924)] // dropped column already gone
    public void IsAlreadyAppliedError_TrueForSchemaAlreadyInPlace(int number) =>
        Assert.True(DatabaseMigrator.IsAlreadyAppliedError(number));

    [Theory]
    [InlineData(208)]   // invalid object name
    [InlineData(207)]   // invalid column name
    [InlineData(547)]   // constraint conflict
    [InlineData(1205)]  // deadlock
    [InlineData(-2)]    // timeout
    public void IsAlreadyAppliedError_FalseForRealFailures(int number) =>
        Assert.False(DatabaseMigrator.IsAlreadyAppliedError(number));
}
