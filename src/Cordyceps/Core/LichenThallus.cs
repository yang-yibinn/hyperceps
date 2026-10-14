using System;
using System.Collections.Generic;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps: Lichen Thalli (github.com/yang-yibinn/Lichen), read without depending on Lichen.
    ///
    /// A Thallus is a Grasshopper group (Lichen's <c>LichenThallusGroup</c> derives from
    /// <see cref="GH_Group"/>) with an author's description and key/value properties, plus one hidden
    /// "Thallus Output" component that is its T output. Both are recognised by Lichen's fixed
    /// component ids, and the description and properties are read through the same field names
    /// Lichen saves in the .gh file. Neither can change without breaking Lichen's own saved files,
    /// so they are a stable contract. If they ever did change, a Thallus would simply read as a plain
    /// group. Without Lichen installed, none of this applies.
    ///
    /// Hyperceps reads Thalli but never changes them: membership, nesting, the hidden output and the
    /// Thallus itself are Lichen's to manage, with its own rules.
    /// </summary>
    public static class LichenThallus
    {
        public static readonly Guid ThallusId = new Guid("4ae5a7b2-c93d-47d2-9f18-a924e1d5fd63");
        public static readonly Guid EndpointId = new Guid("f478f97d-34d4-405d-bc4f-e739dc04b9f7");

        private const string DescriptionKey = "LichenThallusDescription";
        private const string PropertyCountKey = "LichenThallusPropertyCount";
        private const string PropertyKey = "LichenThallusPropertyKey";
        private const string PropertyValueKey = "LichenThallusPropertyValue";

        public static bool IsThallus(IGH_DocumentObject obj) => obj is GH_Group && obj.ComponentGuid == ThallusId;

        /// <summary>The hidden component that is a Thallus's T output.</summary>
        public static bool IsEndpoint(IGH_DocumentObject obj) => obj != null && obj.ComponentGuid == EndpointId;

        /// <summary>The Thallus a hidden output belongs to: the Thallus that has it as a member.</summary>
        public static GH_Group Owner(IEnumerable<IGH_DocumentObject> objects, Guid endpointId) =>
            objects.OfType<GH_Group>().FirstOrDefault(g => IsThallus(g) && g.ObjectIDs.Contains(endpointId));

        /// <summary>
        /// The author's description and properties, read the way Lichen saves them. False (and empty
        /// values) when they can't be read; the Thallus is then shown as a plain group.
        /// </summary>
        public static bool TryReadMetadata(GH_Group thallus, out string description, out List<KeyValuePair<string, string>> properties)
        {
            description = null;
            properties = new List<KeyValuePair<string, string>>();
            try
            {
                var chunk = new GH_LooseChunk("thallus");
                if (!thallus.Write(chunk))
                    return false;

                string text = null;
                if (chunk.TryGetString(DescriptionKey, ref text) && !string.IsNullOrWhiteSpace(text))
                    description = text.Trim();

                int count = 0;
                if (chunk.TryGetInt32(PropertyCountKey, ref count))
                {
                    for (int i = 0; i < count; i++)
                    {
                        string key = null, value = null;
                        if (!chunk.TryGetString(PropertyKey, i, ref key) || string.IsNullOrWhiteSpace(key)) continue;
                        chunk.TryGetString(PropertyValueKey, i, ref value);
                        properties.Add(new KeyValuePair<string, string>(key.Trim(), (value ?? "").Trim()));
                    }
                }
                return true;
            }
            catch (Exception ex) // prawduct:allow prawduct/broad-except -- a third-party object's Write can throw anything; fall back to a plain group
            {
                DebugLog.Warn($"Could not read Lichen Thallus '{thallus.NickName}': {ex.Message}");
                return false;
            }
        }

        public static string MembershipRefusal(string name) =>
            $"'{name}' is a Lichen Thallus. Hyperceps doesn't change Thallus membership: select the objects in Grasshopper and use the Thallus's right-click menu (Add selected objects to Thallus / Remove selected objects from Thallus), or put them in a separate group";

        public static string EditRefusal(string name) =>
            $"'{name}' is a Lichen Thallus. Hyperceps doesn't rename, recolour or delete Thalli; do it in Grasshopper with Lichen (double-click the Thallus border to edit its name and description)";

        public static string EndpointRefusal(string label) =>
            $"{label} is the hidden T output of a Lichen Thallus. Hyperceps doesn't delete or group it; Lichen manages it together with its Thallus";
    }
}
