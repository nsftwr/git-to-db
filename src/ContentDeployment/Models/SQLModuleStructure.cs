using System;
using Newtonsoft.Json;

namespace Content.Models
{
    public class SQLModuleStructure : IEquatable<SQLModuleStructure>
    {
        [JsonProperty("objectId")] public string? ObjectId { get; set; }

        [JsonProperty("commitId")] public string? CommitId { get; set; }

        [JsonProperty("Name")] public string? ModuleName { get; set; }

        [JsonProperty("Description")] public string? Description { get; set; }

        [JsonProperty("Version")] public int? ModuleVersion { get; set; }

        [JsonProperty("Path")] public string? Path { get; set; }

        [JsonProperty("State")] public string? State { get; set; }

        public string? ModuleId => Path!.Replace("/Modules/", "").Replace("/module.json", "");

        public bool Equals(SQLModuleStructure? other)
        {
            return ModuleName == other!.ModuleName
                   && ModuleVersion == other.ModuleVersion
                   && Description == other.Description;
        }
    }
}