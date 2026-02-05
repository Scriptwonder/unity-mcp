using System;

namespace MCPForUnity.Runtime.Tools
{
    /// <summary>
    /// Marks a class as a runtime MCP tool handler.
    /// The class must have a public static HandleCommand(JObject) method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class RuntimeMcpToolAttribute : Attribute
    {
        /// <summary>
        /// Tool name. If null, derived from class name (PascalCase to snake_case).
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Tool description for the LLM.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Whether this tool returns structured output.
        /// </summary>
        public bool StructuredOutput { get; set; } = true;

        public RuntimeMcpToolAttribute() { }

        public RuntimeMcpToolAttribute(string name)
        {
            Name = name;
        }
    }
}
