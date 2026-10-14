using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Cordyceps.Core
{
    /// <summary>
    /// Points a tool at a hand-written JSON Schema embedded in the plugin. The server's schema
    /// generator only describes simple arguments (text, numbers, true/false); a tool whose
    /// arguments are nested JSON (gh_batch) publishes this schema in <c>tools/list</c> instead.
    /// The tool's C# arguments stay strings: an object or array argument reaches them as its JSON text.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class McpInputSchemaAttribute : Attribute
    {
        public McpInputSchemaAttribute(string resourceName)
        {
            ResourceName = resourceName;
        }

        /// <summary>Manifest resource name, e.g. <c>Cordyceps.Tools.Batch.GhBatchSchema.json</c>.</summary>
        public string ResourceName { get; }
    }

    public static class McpInputSchema
    {
        /// <summary>
        /// Read an embedded schema as a <see cref="JsonElement"/>, which the server's serializer
        /// writes out unchanged. Throws when the resource is missing or not a JSON object, so a
        /// broken schema fails loudly at server start rather than reaching a client.
        /// </summary>
        public static JsonElement Load(Assembly assembly, string resourceName)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded schema '{resourceName}' not found");
            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Embedded schema '{resourceName}' is not a JSON object");
            return doc.RootElement.Clone();
        }
    }
}
