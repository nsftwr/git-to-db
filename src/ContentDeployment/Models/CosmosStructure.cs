namespace Content.Models
{
#pragma warning disable CS8618
    public class CosmosStructure
    {
        public string id { get; set; }
        public int? ModuleId { get; set; }
        public int OrderPost { get; set; }
        public string? SectionName { get; set; }
        public string? MdContent { get; set; }
        public string? _self { get; set; }
        public string? CommitId { get; set; }
    }
    
    public record CosmosArchitecture(
        string id,
        string? ModuleId,
        int OrderPost,
        string SectionName,
        string MdContent,
        string _self,
        string CommitId
    );
#pragma warning restore CS8618
}
