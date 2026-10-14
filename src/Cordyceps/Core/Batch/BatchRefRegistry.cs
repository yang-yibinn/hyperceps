using System;
using System.Collections.Generic;
using System.Linq;

namespace Cordyceps.Core.Batch
{
    /// <summary>
    /// Refs that outlive one batch: per document, ref → object GUID, held in memory for the rest
    /// of the Grasshopper session and cleared when the document closes. Nothing is saved in the
    /// .gh file. A ref whose object has since been deleted stays here; using it is an error, and a
    /// new batch may take the name over.
    /// </summary>
    public static class BatchRefRegistry
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<Guid, Dictionary<string, Guid>> _refs = new Dictionary<Guid, Dictionary<string, Guid>>();

        public static bool TryGet(Guid documentId, string name, out Guid objectId)
        {
            lock (_lock)
            {
                objectId = Guid.Empty;
                return _refs.TryGetValue(documentId, out var map) && map.TryGetValue(name, out objectId);
            }
        }

        public static void Set(Guid documentId, string name, Guid objectId)
        {
            lock (_lock)
            {
                if (!_refs.TryGetValue(documentId, out var map))
                    _refs[documentId] = map = new Dictionary<string, Guid>(StringComparer.Ordinal);
                map[name] = objectId;
            }
        }

        /// <summary>All refs on a document, for "did you mean" suggestions.</summary>
        public static List<KeyValuePair<string, Guid>> All(Guid documentId)
        {
            lock (_lock)
            {
                return _refs.TryGetValue(documentId, out var map)
                    ? map.ToList()
                    : new List<KeyValuePair<string, Guid>>();
            }
        }

        public static void Clear(Guid documentId)
        {
            lock (_lock)
            {
                _refs.Remove(documentId);
            }
        }
    }
}
