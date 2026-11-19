using System;
using System.Collections.Generic;

namespace Forge.SolidWorks
{
    /// <summary>
    /// SafeCadDefaults — fail-closed fallback intents for free-tier LLM tool calls.
    /// Free models often emit tool calls with an empty or missing "intent" argument.
    /// This class injects a conservative, handler-parseable default intent per tool so
    /// the dispatch path has something to parse instead of failing on an empty string.
    /// Unknown tools fail closed: no invented text is ever returned.
    /// </summary>
    public static class SafeCadDefaults
    {
        private static readonly Dictionary<string, string> Defaults =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["create_plate"] = "create a 100x60x8 mm plate",
                ["add_hole"] = "add an 8mm hole",
                ["add_boss"] = "add a 12mm boss 10mm tall",
                ["add_pocket"] = "cut a 20x10mm pocket 5mm deep",
                ["create_part"] = "create a new part",
                ["create_assembly"] = "create a new assembly",
                ["create_drawing"] = "create a new drawing",
                ["create_sketch"] = "start a sketch on the front plane",
                ["add_counterbore"] = "add a counterbore for an M6 cap screw",
                ["add_countersink"] = "add a countersink for an M6 flat head",
                ["fillet_chamfer"] = "fillet all the sharp edges 2mm",
                ["get_mass_props"] = "get mass properties",
                ["rebuild_document"] = "rebuild the model",
                ["capture_viewport"] = "capture an isometric view of the part",
                ["list_entities"] = "list the entities",
            };

        /// <summary>
        /// Return the given parsed intent when it is non-empty, otherwise the safe default
        /// intent registered for the tool. Unknown tools return "" (fail closed).
        /// </summary>
        public static string HealIntent(string toolName, string parsedIntent)
        {
            if (!string.IsNullOrWhiteSpace(parsedIntent)) return parsedIntent;
            return HasDefault(toolName) ? Defaults[toolName] : "";
        }

        /// <summary>
        /// Whether a safe default intent exists for the given tool name.
        /// </summary>
        public static bool HasDefault(string toolName)
        {
            return Defaults.ContainsKey(toolName ?? "");
        }

        /// <summary>
        /// The full tool-name -> default-intent map (useful for tests and telemetry).
        /// </summary>
        public static IReadOnlyDictionary<string, string> AllDefaults()
        {
            return Defaults;
        }
    }
}
