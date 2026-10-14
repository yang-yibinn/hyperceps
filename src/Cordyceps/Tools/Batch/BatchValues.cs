using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cordyceps.Core.Batch;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cordyceps.Tools.Batch
{
    /// <summary>
    /// Turns a <see cref="BatchValue"/> into the Grasshopper data an input stores ("persistent
    /// data" — the value you'd type into an input's right-click Set menu). Conversion is done up
    /// front, in memory, so a value an input can't take is a validation error and nothing changes.
    /// </summary>
    internal static class BatchValues
    {
        /// <summary>The data type an input stores (e.g. GH_Number), or null when it can't store values.</summary>
        public static Type StoredType(IGH_Param p)
        {
            for (var t = p.GetType(); t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(GH_PersistentParam<>))
                    return t.GetGenericArguments()[0];
            }
            return null;
        }

        /// <summary>
        /// Convert every item of <paramref name="value"/> for <paramref name="p"/>. A null value
        /// yields an empty list (clear). Returns false with a message naming the input and the value.
        /// </summary>
        public static bool TryConvert(BatchValue value, IGH_Param p, string label, out List<IGH_Goo> items, out string error)
        {
            items = new List<IGH_Goo>();
            error = null;

            var stored = StoredType(p);
            if (stored == null)
            {
                error = $"input '{p.NickName}' on {label} can't hold a set value; wire something into it";
                return false;
            }

            foreach (var v in value.AsList())
            {
                var goo = ConvertOne(v, stored, out var why);
                if (goo == null)
                {
                    error = $"input '{p.NickName}' on {label} takes {p.TypeName}; {why}";
                    return false;
                }
                items.Add(goo);
            }
            return true;
        }

        private static IGH_Goo ConvertOne(BatchValue v, Type stored, out string why)
        {
            why = null;
            if (stored == typeof(GH_Integer) && v.Kind == BatchValueKind.Number && !v.IsWhole)
            {
                why = $"{v.Describe()} isn't a whole number";
                return null;
            }

            object raw = Raw(v, stored);
            IGH_Goo natural = Natural(v);

            if (stored.IsInterface || stored.IsAbstract)
            {
                if (natural != null && stored.IsInstanceOfType(natural))
                    return natural;
                why = $"{v.Describe()} can't be used there";
                return null;
            }

            IGH_Goo goo;
            try { goo = Activator.CreateInstance(stored) as IGH_Goo; }
            catch (Exception) { goo = null; }

            if (goo != null && (goo.CastFrom(raw) || (natural != null && goo.CastFrom(natural))) && goo.IsValid)
                return goo;

            why = $"{v.Describe()} can't be converted to it";
            return null;
        }

        private static object Raw(BatchValue v, Type stored)
        {
            switch (v.Kind)
            {
                case BatchValueKind.Number:
                    return stored == typeof(GH_Integer) ? (object)(int)Math.Round(v.Number) : v.Number;
                case BatchValueKind.Boolean: return v.Boolean;
                case BatchValueKind.Text: return v.Text;
                case BatchValueKind.Point: return new Point3d(v.Coords[0], v.Coords[1], v.Coords[2]);
                case BatchValueKind.Vector: return new Vector3d(v.Coords[0], v.Coords[1], v.Coords[2]);
                case BatchValueKind.Domain: return new Interval(v.Coords[0], v.Coords[1]);
                default: return null;
            }
        }

        private static IGH_Goo Natural(BatchValue v)
        {
            switch (v.Kind)
            {
                case BatchValueKind.Number: return new GH_Number(v.Number);
                case BatchValueKind.Boolean: return new GH_Boolean(v.Boolean);
                case BatchValueKind.Text: return new GH_String(v.Text);
                case BatchValueKind.Point: return new GH_Point(new Point3d(v.Coords[0], v.Coords[1], v.Coords[2]));
                case BatchValueKind.Vector: return new GH_Vector(new Vector3d(v.Coords[0], v.Coords[1], v.Coords[2]));
                case BatchValueKind.Domain: return new GH_Interval(new Interval(v.Coords[0], v.Coords[1]));
                default: return null;
            }
        }

        /// <summary>
        /// Replace an input's stored values (an empty list clears them). The input is cleared first:
        /// SetPersistentData adds to whatever the input already holds, so an input with a default
        /// (Series Start = 0, Trim Tree Depth = 1) would otherwise end up with [0, 1] or [1, 1].
        /// </summary>
        public static void Store(IGH_Param p, List<IGH_Goo> items)
        {
            var type = p.GetType();
            var data = type.GetProperty("PersistentData")?.GetValue(p);
            data?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(data, null);
            if (items.Count == 0)
                return;
            var set = type.GetMethod("SetPersistentData", new[] { typeof(object[]) });
            set.Invoke(p, new object[] { items.Cast<object>().ToArray() });
        }

        /// <summary>
        /// The undo record Grasshopper itself makes for a change to an input's stored values. The
        /// helper is generic over the stored type, so it is called through reflection.
        /// </summary>
        public static Grasshopper.Kernel.Undo.GH_UndoRecord PersistentDataEvent(GH_Document doc, IGH_Param p)
        {
            var stored = StoredType(p);
            var method = doc.UndoUtil.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "CreatePersistentDataEvent" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(stored);
            return (Grasshopper.Kernel.Undo.GH_UndoRecord)method.Invoke(doc.UndoUtil, new object[] { "Set value", p });
        }
    }
}
