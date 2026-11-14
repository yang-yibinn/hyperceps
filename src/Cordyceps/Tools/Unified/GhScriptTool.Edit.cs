using System.Collections.Generic;
using Cordyceps.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cordyceps.Tools.Unified
{
    /// <summary>
    /// Hyperceps: gh_script(action='edit') — replace exact passages of a script's current source and
    /// write the result exactly as action='set' does (same rebuild, verification, lost-connection
    /// report and solve=true results). The agent sends a few lines instead of the whole script;
    /// the source is never echoed back. See <see cref="ScriptEdits"/>.
    /// </summary>
    public partial class GhScriptTool
    {
        private string ActionEdit(string id, string edits, bool solve)
        {
            var errors = new List<string>();
            var parsed = ScriptEdits.Parse(edits, errors);
            if (errors.Count > 0)
                return ToolHelpers.ErrorResponse(string.Join("; ", errors) + ". Nothing was changed");

            return _context.ExecuteOnUiThread(() =>
            {
                if (!ToolHelpers.TryGetUnprotectedComponent(_context, id, out var component, out var error))
                    return ToolHelpers.ErrorResponse(error);
                if (!IsScriptComponent(component))
                    return ToolHelpers.ErrorResponse($"Component '{component.NickName}' is not a script component.");

                var source = TryGetScriptSource(component);
                if (source == null)
                    return ToolHelpers.ErrorResponse("Could not read the script's source, so nothing was changed. Write the whole script with action='set'");

                var result = ScriptEdits.Apply(source, parsed);
                if (!result.Ok)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = string.Join("; ", result.Errors) + ". Nothing was changed",
                        sourceLength = source.Length
                    });
                }

                // Already on the UI thread, so this runs inline: the same write as action='set'.
                var written = JObject.Parse(ActionSet(id, result.Source, solve));
                ScriptEdits.Annotate(written, parsed.Count, result);   // only when the write succeeded
                return written.ToString(Formatting.None);
            });
        }
    }
}
