using System.Text.Json.Serialization;

namespace AzureRestAPIExamples.Models;

public record ScimUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("active")] bool Active)
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = ["urn:ietf:params:scim:schemas:core:2.0:User"];
}
