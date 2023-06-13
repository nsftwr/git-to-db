using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Content.Models
{
    [JsonConverter(typeof(JsonPathConverter))]
    public class GitStructure
    {
        [JsonProperty("objectId")]
        public string ObjectIdFresh
        {
            get => null!;
            set => ObjectId = value;
        }

        [JsonProperty("gitObjectType")]
        public string? GitObjectTypeFresh
        {
            get => null;
            set => GitObjectType = value;
        }

        [JsonProperty("path")]
        public string? PathFresh
        {
            get => null;
            set => Path = value;
        }

        [JsonProperty("latestProcessedChange.commitId")]
        public string? CommitIdFresh
        {
            get => null;
            set => CommitId = value;
        }

        [JsonProperty("item.objectId")] public string? ObjectId { get; set; }

        [JsonProperty("item.gitObjectType")] public string? GitObjectType { get; set; }

        [JsonProperty("item.path")] public string? Path { get; set; }

        [JsonProperty("item.commitId")] public string? CommitId { get; set; }

        [JsonProperty("changeType")] public string? ChangeType { get; set; } = "add";

        public string? Id
        {
            get
            {
                var pattern = @"/Modules/(?<moduleName>[\w\d']+)/(?<orderPost>[\w\d']+)-(?<sectionName>[\w\d']+)\.md";
                var info = Regex.Match(Path!, pattern);
                return $"{info.Groups["moduleName"].Value}-{info.Groups["sectionName"].Value}".ToLower();
            }
        }
    }
    
#pragma warning disable CS8765
    class JsonPathConverter : JsonConverter
    {
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            object targetObj = Activator.CreateInstance(objectType)!;

            foreach (PropertyInfo prop in objectType.GetProperties().Where(p => p.CanRead && p.CanWrite))
            {
                JsonPropertyAttribute att = prop.GetCustomAttributes(true)
                    .OfType<JsonPropertyAttribute>()
                    .FirstOrDefault()!;

                string jsonPath = (att != null! ? att.PropertyName : prop.Name)!;
                JToken token = jo.SelectToken(jsonPath!)!;

                if (token != null! && token.Type != JTokenType.Null)
                {
                    object value = token.ToObject(prop.PropertyType, serializer)!;
                    prop.SetValue(targetObj, value, null);
                }
            }

            return targetObj;
        }

        public override bool CanConvert(Type objectType)
        {
            // CanConvert is not called when [JsonConverter] attribute is used
            return false;
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
#pragma warning restore CS8765
}