using System;
using System.Collections.Generic;
using System.Linq;
using Cordyceps.Core;
using Cordyceps.Core.Batch;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// Every installed Grasshopper type as <see cref="TypeCandidate"/>s for the strict type rule,
    /// cached until the set of installed types changes (a plugin loading adds proxies), plus the
    /// object factory gh_batch uses for each kind of component entry.
    /// </summary>
    internal static class BatchCatalog
    {
        private static readonly object _lock = new object();
        private static TypeCatalog _cached;
        private static int _cachedCount = -1;

        /// <summary>Component GUIDs for the shortcut objects, so results can name their type.</summary>
        public static readonly Guid PanelGuid = new Guid("59e0b89a-e487-49f8-bab8-b5bab16be14c");
        public static readonly Guid SliderGuid = new Guid("57da07bd-ecab-415d-9d86-af36d7073abc");
        public static readonly Guid ValueListGuid = new Guid("00027467-0d24-4fa7-b178-8dc0ac5f42ec");
        public static readonly Guid ToggleGuid = new Guid("2e78987b-9dfb-42a2-8b76-3923ac8bd91a");

        public static TypeCatalog Get()
        {
            var proxies = Instances.ComponentServer.ObjectProxies;
            lock (_lock)
            {
                if (_cached != null && _cachedCount == proxies.Count)
                    return _cached;

                var libraries = new Dictionary<Guid, string>();
                var deprecation = DeprecationRegistry.Instance;
                var candidates = new List<TypeCandidate>(proxies.Count);

                foreach (var proxy in proxies)
                {
                    if (proxy?.Desc == null) continue;

                    bool deprecated = proxy.Obsolete || deprecation.IsDeprecated(proxy.Guid);
                    if (!deprecated)
                    {
                        try { deprecated = proxy.Type?.Name.IndexOf("OBSOLETE", StringComparison.OrdinalIgnoreCase) >= 0; }
                        catch (Exception) { /* a proxy that can't report its type is judged on its flags alone */ }
                    }

                    if (!libraries.TryGetValue(proxy.LibraryGuid, out var plugin))
                    {
                        var info = Instances.ComponentServer.FindAssembly(proxy.LibraryGuid);
                        plugin = info == null ? null : info.IsCoreLibrary ? "Grasshopper" : info.Name;
                        libraries[proxy.LibraryGuid] = plugin;
                    }

                    candidates.Add(new TypeCandidate
                    {
                        Name = proxy.Desc.Name,
                        Category = proxy.Desc.Category,
                        SubCategory = proxy.Desc.SubCategory,
                        Guid = proxy.Guid,
                        Deprecated = deprecated,
                        Plugin = plugin
                    });
                }

                _cached = new TypeCatalog(candidates);
                _cachedCount = proxies.Count;
                return _cached;
            }
        }

        public static IGH_DocumentObject Emit(Guid componentGuid) =>
            Instances.ComponentServer.EmitObject(componentGuid) as IGH_DocumentObject;

        public static GH_NumberSlider NewSlider()
        {
            var slider = new GH_NumberSlider();
            slider.SetInitCode("0.0 < 0.5 < 1.0");
            return slider;
        }

        /// <summary>The "Category/Subcategory/Name" of an object's type, for dry-run results.</summary>
        public static string PathOf(IGH_DocumentObject obj)
        {
            var c = Get().ByGuid(obj.ComponentGuid);
            return c != null ? c.PathText : obj.Name;
        }

        /// <summary>
        /// Grasshopper 8's C# and Python components. Their ports are set with gh_script, so port
        /// counts on them are refused with a pointer there.
        /// </summary>
        public static bool IsScriptComponent(IGH_DocumentObject obj)
        {
            var ns = obj.GetType().Namespace ?? string.Empty;
            return ns.StartsWith("RhinoCodePlugin", StringComparison.Ordinal)
                || obj.GetType().Name.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0
                   && obj is IGH_VariableParameterComponent;
        }

        /// <summary>Tree components the spec prefers as port settings, with the matching setting.</summary>
        public static string PortSettingFor(IGH_DocumentObject obj)
        {
            if (!string.Equals(obj.Category, "Sets", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Get().ByGuid(obj.ComponentGuid)?.Category, "Sets", StringComparison.OrdinalIgnoreCase))
                return null;

            switch (obj.Name)
            {
                case "Graft Tree": return "graft";
                case "Flatten Tree": return "flatten";
                case "Simplify Tree": return "simplify";
                case "Reverse List": return "reverse";
                default: return null;
            }
        }
    }
}
