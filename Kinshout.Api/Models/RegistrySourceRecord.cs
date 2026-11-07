namespace Kinshout.Api.Models;

/// <summary>
/// An Ops override of a registry row, or a source added from the desk.
/// A null definition means the JSON file still owns the row and only the import flag changed.
/// </summary>
public class RegistrySourceRecord
{
    public string Name { get; set; } = "";
    public bool Import { get; set; }
    public string? DefinitionJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
