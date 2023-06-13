using Newtonsoft.Json;

namespace Content.Models
{
    public class SQLCourseStructure : IEquatable<SQLCourseStructure>
    {
        [JsonProperty("objectId")]
        public string? ObjectId { get; set; }
        
        [JsonProperty("commitId")]
        public string? CommitId { get; set; }

        [JsonProperty("Name")]
        public string? CourseName { get; set; }

        [JsonProperty("Description")]
        public string? Description { get; set; }
        
        [JsonProperty("Category")]
        public string? Category { get; set; }
        
        [JsonProperty("Length")]
        public int? CourseLength { get; set; }
        
        [JsonProperty("Modules")]
        public string[]? Modules { get; set; }

        [JsonProperty("Path")]
        public string? Path { get; set; }
        
        [JsonProperty("State")]
        public string? State { get; set; }

        public bool Equals(SQLCourseStructure? other)
        {
            return CourseName == other!.CourseName
                && Description == other.Description;
        }

        public string CourseId => Path!.Replace("/Courses/", "").Replace("/course.json", "");
    }
}
