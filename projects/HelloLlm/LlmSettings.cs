namespace HelloLlm
{
    public class LlmSettings
    {
        public float? Temperature { get; init; }
        public float? TopP { get; init; }
        public int? MaxTokens { get; init; }
        public LlmOutputFormat? OutputFormat { get; init; }
        /// <summary>
        /// JsonSchema 模式下必填，用 typeof(T) 传
        /// </summary>
        public Type? SchemaType { get; init; }
    }
    public enum LlmOutputFormat
    {
        /// <summary>
        /// 自由文本，默认行为。
        /// </summary>
        Text,
        /// <summary>
        /// 合法 JSON，但不限定字段结构。
        /// </summary>
        Json,

        JsonSchema
    }
}
