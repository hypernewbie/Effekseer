// [UAA] - START - efkc from-spec: author .efkefc from a strict JSON spec
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effekseer;
using Effekseer.Data;

namespace EffekseerValidate
{
	// from-spec SPEC.json --output OUT.efkefc [--force]: author an editor-side
	// effect file from a strict JSON spec. Authoring runs entirely in memory
	// through the editor's own Core APIs (Core.New -> typed public value
	// setters -> Core.SaveTo), so the output is exactly what the editor would
	// write, and it reloads and validates identically.
	//
	// Spec shape (v1):
	//   {
	//     "name": "Node",                 // optional node display name
	//     "settings": {                   // optional; top level only
	//       "start_frame": 0,             //   integer >= 0, <= effective end
	//       "end_frame": 120,             //   integer >= 0, >= effective start
	//       "is_loop": true               //   boolean
	//     },
	//     "common": {                     // optional; every field optional
	//       "max_generation": 1,          //   integer >= 1  -> CommonValues.MaxGeneration
	//       "spawn_count": 1,             //   integer >= 0, or {center,min,max}
	//                                     //     -> CommonValues.Generation.TriggerCount
	//       "lifetime": 100,              //   integer >= 1, or {center,min,max}
	//                                     //     -> CommonValues.Life
	//       "color": [1.0,0.5,0.5,1.0]    //   optional RGBA normalized to [0,1];
	//                                     //     overall render color
	//                                     //     (sprite/model/ribbon only)
	//     },
	//     "renderer": {                   // optional
	//       "type": "sprite",             //   sprite|ribbon|ring|model|track|none
	//       "color_texture": "Textures/hero.png" // optional relative path
	//     },
	//     "children": [ NODE, ... ]       // optional; same shape minus "settings"
	//   }
	//
	// Integer fields reject fractional JSON numbers (no silent rounding) and
	// out-of-range values; every field is type-checked, unknown fields are
	// rejected, and semantically invalid combinations (color on a track/ring/
	// none renderer, color_texture on a none renderer, and color_texture
	// paths that are absolute on ANY platform - POSIX '/', Windows drive
	// "X:", UNC "\\" - or contain NUL / non-portable characters, a Windows
	// reserved device name (CON, PRN, AUX, NUL, COM1..COM9, LPT1..LPT9,
	// case-insensitively, with or without extension) or a component ending
	// in a period or space) fail with a message naming the exact spec
	// location. The spec file itself must be
	// valid UTF-8: a UTF-8 BOM is tolerated, but invalid byte sequences are
	// rejected, never silently replaced with U+FFFD.
	//
	// Settings frame bounds are validated on the EFFECTIVE values: an omitted
	// start_frame defaults to 0 and an omitted end_frame defaults to 120
	// (Core.New's own defaults), and the effective start must not exceed the
	// effective end. A lone start_frame above the default end or a lone
	// end_frame below the default start is rejected here rather than silently
	// clamped by Core's StartFrame/EndFrame setters.
	//
	// spawn_count and lifetime each accept either a plain integer (a fixed
	// value: center = min = max) or an object {center,min,max} of integers
	// with min <= center <= max, mapped through the public IntWithRandom
	// setters (SetMin/SetMax/SetCenter). The editor's setters derive center
	// from min/max (center = (min+max)/2) or keep a symmetric amplitude
	// around center, so only triples with center == (min+max)/2 can be
	// represented exactly; any other triple is rejected with an explicit
	// error instead of being silently narrowed.
	//
	// common.color is normalized: exactly four finite numbers in [0,1]. The
	// conversion to the editor's integer 0..255 channels is
	// (int)Math.Round(v * 255.0, MidpointRounding.AwayFromZero), so 1.0 ->
	// 255 and 0.5 -> 128, applied BEFORE the typed Int setters are called.
	//
	// The output root path is established (Core.Root.SetFullPath) BEFORE any
	// relative resource path is applied, so a relative color_texture resolves
	// against the destination directory and is stored as the same relative
	// reference. This ordering is the fix for the "SetRelativePath before
	// SaveTo" defect: with an empty root path, SetRelativePath silently stores
	// the relative string as if it were absolute.
	//
	// Saving is transactional: the effect is authored into a UNIQUE sibling
	// temp file (exclusively created, so concurrent runs and stale temps can
	// never collide or be silently reused), the temp is validated and its
	// texture references verified (ordered multiset: the same references in
	// the same order, duplicates counted - including the empty case, where
	// the reloaded output must prove it carries no texture reference)
	// BEFORE it is promoted with a force-aware move over the destination.
	// Every exit before promotion deletes this run's own temp; any failure
	// leaves the destination - and any pre-existing file under --force -
	// untouched.
	public static class FromSpecCommand
	{
		sealed class NodeSpec
		{
			public bool HasName;
			public string Name = "";
			public bool HasMaxGeneration;
			public int MaxGeneration;
			public bool HasSpawnCount;
			public int SpawnCount;
			public bool HasSpawnCountRange;
			public int SpawnCountMin;
			public int SpawnCountMax;
			public bool HasLifetime;
			public int Lifetime;
			public bool HasLifetimeRange;
			public int LifetimeMin;
			public int LifetimeMax;
			public bool HasColor;
			public double[] Color = new double[4];
			public bool HasRendererType;
			public RendererValues.ParamaterType RendererType;
			public bool HasColorTexture;
			public string ColorTexture = "";
			public List<NodeSpec> Children = new List<NodeSpec>();
			// Top level only; null everywhere else.
			public SpecSettings Settings;
		}

		sealed class SpecSettings
		{
			public bool HasStartFrame;
			public int StartFrame;
			public bool HasEndFrame;
			public int EndFrame;
			public bool HasIsLoop;
			public bool IsLoop;
		}

		public static int Run(Args args)
		{
			if (string.IsNullOrEmpty(args.Output))
			{
				Console.Error.WriteLine("efkc: from-spec requires --output OUT.efkefc");
				return 64;
			}
			var specPath = args.Paths[0];
			var output = args.Output;
			string fullOutput;
			try
			{
				fullOutput = Path.GetFullPath(output);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"efkc: invalid output path {output}: {ex.Message}");
				return 1;
			}

			if ((File.Exists(fullOutput) || Directory.Exists(fullOutput)) && !args.Force)
			{
				Console.Error.WriteLine($"efkc: output exists: {output} (pass --force to overwrite)");
				return 1;
			}

			if (!TryReadSpec(specPath, out var spec, out var error))
			{
				Console.Error.WriteLine($"efkc: {error}");
				return 1;
			}

			// Authoring below mutates the process-global editor tree; every
			// failure before the final save leaves no file behind.
			Core.New();

			// Establish the output root path BEFORE applying any relative
			// resource path: Value.Path.SetRelativePath resolves against the
			// root's absolute path, and an empty root path silently stores the
			// relative string as if it were absolute (the defect this ordering
			// fixes). Core.SaveTo re-establishes the same path at save time.
			Core.Root.SetFullPath(fullOutput);

			if (spec.Settings != null)
			{
				// Validate the EFFECTIVE frame bounds after applying defaults:
				// Core.StartFrame/EndFrame freshly default to 0/120 here, and
				// Core's own setters would silently clamp an invalid pair, so
				// the spec is rejected instead. This covers both-present
				// inversions, a lone start_frame above the default end, and a
				// lone end_frame below the default start.
				var effectiveStart = spec.Settings.HasStartFrame ? spec.Settings.StartFrame : Core.StartFrame;
				var effectiveEnd = spec.Settings.HasEndFrame ? spec.Settings.EndFrame : Core.EndFrame;
				if (effectiveStart > effectiveEnd)
				{
					Console.Error.WriteLine(
						$"efkc: spec.settings: effective start_frame ({effectiveStart}) must not exceed " +
						$"effective end_frame ({effectiveEnd})");
					return 1;
				}
				if (spec.Settings.HasStartFrame) Core.StartFrame = spec.Settings.StartFrame;
				if (spec.Settings.HasEndFrame) Core.EndFrame = spec.Settings.EndFrame;
				if (spec.Settings.HasIsLoop) Core.IsLoop = spec.Settings.IsLoop;
			}

			// Core.New() always creates exactly one default child node; the
			// spec describes that node (its name, values, renderer and
			// children). The NodeRoot itself is not user-configurable.
			var rootNode = Core.Root.Children[0];
			if (!ApplyNode(rootNode, spec, out error))
			{
				Console.Error.WriteLine($"efkc: {error}");
				return 1;
			}

			var saveStatus = SaveTransactional(fullOutput, spec, args.Force, out error);
			if (saveStatus == 1)
			{
				Console.Error.WriteLine($"efkc: {error}");
				return 1;
			}
			return saveStatus;
		}

		// ---- spec reading / strict validation --------------------------------

		static bool TryReadSpec(string path, out NodeSpec spec, out string error)
		{
			spec = null;
			error = "";

			if (!File.Exists(path))
			{
				error = $"spec file not found: {path}";
				return false;
			}

			byte[] raw;
			try
			{
				raw = File.ReadAllBytes(path);
			}
			catch (Exception ex)
			{
				error = $"could not read spec {path}: {ex.GetType().Name}: {ex.Message}";
				return false;
			}

			// Strict UTF-8: invalid byte sequences are rejected - the default
			// decoder would silently substitute U+FFFD and could turn a mangled
			// spec into "valid" JSON. A UTF-8 BOM (EF BB BF) is an encoding
			// artifact, not content, and Windows editors commonly write one;
			// JsonDocument rejects it, so skip it explicitly before decoding.
			// (Encoding.UTF8.GetString does NOT consume the BOM.)
			int bomOffset = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
			string text;
			try
			{
				text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true)
					.GetString(raw, bomOffset, raw.Length - bomOffset);
			}
			catch (System.Text.DecoderFallbackException)
			{
				error = $"spec {path} is not valid UTF-8";
				return false;
			}

			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(text);
			}
			catch (JsonException ex)
			{
				error = $"spec {path} is not valid JSON: {ex.Message}";
				return false;
			}

			using (doc)
			{
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object)
				{
					error = $"spec {path} must be a JSON object";
					return false;
				}

				spec = new NodeSpec();
				return ParseNode(root, spec, allowSettings: true, ctx: "spec", out error);
			}
		}

		static bool ParseNode(JsonElement el, NodeSpec spec, bool allowSettings, string ctx, out string error)
		{
			error = "";
			if (el.ValueKind != JsonValueKind.Object)
			{
				error = $"{ctx} must be a JSON object";
				return false;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var prop in el.EnumerateObject())
			{
				if (!seen.Add(prop.Name))
				{
					error = $"{ctx}: duplicate field '{prop.Name}'";
					return false;
				}
				switch (prop.Name)
				{
					case "name":
						if (prop.Value.ValueKind != JsonValueKind.String)
						{
							error = $"{ctx}.name must be a string";
							return false;
						}
						spec.Name = prop.Value.GetString();
						spec.HasName = true;
						break;
					case "settings":
						if (!allowSettings)
						{
							error = $"{ctx}.settings is only valid at the top level of the spec";
							return false;
						}
						if (!ParseSettings(prop.Value, ctx + ".settings", out spec.Settings, out error))
							return false;
						break;
					case "common":
						if (!ParseCommon(prop.Value, spec, ctx + ".common", out error))
							return false;
						break;
					case "renderer":
						if (!ParseRenderer(prop.Value, spec, ctx + ".renderer", out error))
							return false;
						break;
					case "children":
						if (!ParseChildren(prop.Value, spec, ctx + ".children", out error))
							return false;
						break;
					default:
						error = $"{ctx}: unknown field '{prop.Name}'";
						return false;
				}
			}

			// Cross-field semantic checks: the renderer type gates which fields
			// are meaningful. The default renderer is sprite.
			var type = spec.HasRendererType ? spec.RendererType : RendererValues.ParamaterType.Sprite;
			if (spec.HasColor && !ColorSupported(type))
			{
				error = $"{ctx}.common.color is not supported for renderer type '{RenderTypeName(type)}' " +
						$"(supported: sprite, model, ribbon)";
				return false;
			}
			if (spec.HasColorTexture && type == RendererValues.ParamaterType.None)
			{
				error = $"{ctx}.renderer.color_texture is not supported for renderer type 'none'";
				return false;
			}
			return true;
		}

		static bool ParseSettings(JsonElement el, string ctx, out SpecSettings settings, out string error)
		{
			settings = new SpecSettings();
			error = "";
			if (el.ValueKind != JsonValueKind.Object)
			{
				error = $"{ctx} must be an object";
				return false;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var prop in el.EnumerateObject())
			{
				if (!seen.Add(prop.Name))
				{
					error = $"{ctx}: duplicate field '{prop.Name}'";
					return false;
				}
				switch (prop.Name)
				{
					case "start_frame":
						if (!TryInt(prop.Value, ctx + ".start_frame", 0, int.MaxValue, out settings.StartFrame, out error))
							return false;
						settings.HasStartFrame = true;
						break;
					case "end_frame":
						if (!TryInt(prop.Value, ctx + ".end_frame", 0, int.MaxValue, out settings.EndFrame, out error))
							return false;
						settings.HasEndFrame = true;
						break;
					case "is_loop":
						if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
						{
							error = $"{ctx}.is_loop must be a boolean";
							return false;
						}
						settings.IsLoop = prop.Value.GetBoolean();
						settings.HasIsLoop = true;
						break;
					default:
						error = $"{ctx}: unknown field '{prop.Name}'";
						return false;
				}
			}

			// The start <= end check is deliberately NOT here: it must run
			// against the effective values (omitted keys default to Core's
			// 0/120), which only exist after Core.New, so Run validates them
			// after applying defaults.
			return true;
		}

		static bool ParseCommon(JsonElement el, NodeSpec spec, string ctx, out string error)
		{
			error = "";
			if (el.ValueKind != JsonValueKind.Object)
			{
				error = $"{ctx} must be an object";
				return false;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var prop in el.EnumerateObject())
			{
				if (!seen.Add(prop.Name))
				{
					error = $"{ctx}: duplicate field '{prop.Name}'";
					return false;
				}
				switch (prop.Name)
				{
					case "max_generation":
						if (!TryInt(prop.Value, ctx + ".max_generation", 1, int.MaxValue, out spec.MaxGeneration, out error))
							return false;
						spec.HasMaxGeneration = true;
						break;
					case "spawn_count":
						if (!TryIntOrRandom(prop.Value, ctx + ".spawn_count", 0,
							out spec.SpawnCount, out spec.SpawnCountMin, out spec.SpawnCountMax,
							out spec.HasSpawnCountRange, out error))
							return false;
						spec.HasSpawnCount = true;
						break;
					case "lifetime":
						if (!TryIntOrRandom(prop.Value, ctx + ".lifetime", 1,
							out spec.Lifetime, out spec.LifetimeMin, out spec.LifetimeMax,
							out spec.HasLifetimeRange, out error))
							return false;
						spec.HasLifetime = true;
						break;
					case "color":
						if (!TryColor(prop.Value, ctx + ".color", spec.Color, out error))
							return false;
						spec.HasColor = true;
						break;
					default:
						error = $"{ctx}: unknown field '{prop.Name}'";
						return false;
				}
			}
			return true;
		}

		static bool ParseRenderer(JsonElement el, NodeSpec spec, string ctx, out string error)
		{
			error = "";
			if (el.ValueKind != JsonValueKind.Object)
			{
				error = $"{ctx} must be an object";
				return false;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var prop in el.EnumerateObject())
			{
				if (!seen.Add(prop.Name))
				{
					error = $"{ctx}: duplicate field '{prop.Name}'";
					return false;
				}
				switch (prop.Name)
				{
					case "type":
						if (prop.Value.ValueKind != JsonValueKind.String)
						{
							error = $"{ctx}.type must be a string (sprite, ribbon, ring, model, track, none)";
							return false;
						}
						var typeName = prop.Value.GetString();
						if (!TryRendererType(typeName, out spec.RendererType))
						{
							error = $"{ctx}.type: unknown renderer type '{typeName}' (expected sprite, ribbon, ring, model, track, none)";
							return false;
						}
						spec.HasRendererType = true;
						break;
					case "color_texture":
						if (prop.Value.ValueKind != JsonValueKind.String)
						{
							error = $"{ctx}.color_texture must be a non-empty string";
							return false;
						}
						if (!TryRelativeTexturePath(prop.Value.GetString(), ctx + ".color_texture", out var texturePath, out error))
							return false;
						spec.ColorTexture = texturePath;
						spec.HasColorTexture = true;
						break;
					default:
						error = $"{ctx}: unknown field '{prop.Name}'";
						return false;
				}
			}
			return true;
		}

		// color_texture is a RELATIVE path, stored as a portable reference and
		// resolved against the output file's directory. Rejection is
		// platform-independent: a path that is absolute or invalid on ANY
		// mainstream OS is rejected everywhere, so one spec authors the same
		// reference on every platform. Backslashes are normalized to forward
		// slashes (matching Effekseer's own SetRelativePath normalization and
		// the retarget command), so the stored reference is byte-identical
		// regardless of the host OS or the author's separator habits.
		// Windows-invalid COMPONENTS are also rejected on every host: a
		// component ending in a period or space (Windows silently strips
		// them, so the reference resolves to a different file on Windows
		// than on POSIX) and any component whose base name is a Windows
		// reserved device name (CON, PRN, AUX, NUL, COM1..COM9, LPT1..LPT9,
		// case-insensitively, with or without an extension). Legitimate
		// relative paths (subdirectories, dotfiles, non-reserved lookalikes
		// like com10.png or con2.png, ".."/"." segments) are preserved.
		static bool TryRelativeTexturePath(string path, string ctx, out string normalized, out string error)
		{
			normalized = "";
			error = "";

			if (string.IsNullOrEmpty(path))
			{
				error = $"{ctx} must be a non-empty string";
				return false;
			}

			if (path.IndexOf('\0') >= 0)
			{
				error = $"{ctx} must be a relative path; NUL characters are not allowed";
				return false;
			}

			// Rooted on at least one platform: POSIX '/', the Windows
			// drive-absolute backslash prefix, UNC "\\", a Windows drive
			// "X:" (absolute or drive-relative), or a double-slash prefix
			// (POSIX absolute, UNC-like on Windows). Path.IsPathRooted alone
			// is not portable: on POSIX it does not see "C:\x" or "\\srv\x".
			if (path.StartsWith("/", StringComparison.Ordinal)
				|| path.StartsWith("\\", StringComparison.Ordinal)
				|| (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
			{
				error = $"{ctx} must be a relative path (resolved against the output file's directory), " +
						$"got: {EscapeForMessage(path)}";
				return false;
			}

			// Characters that cannot appear in a file name on Windows (plus
			// control characters everywhere): a spec that authors cleanly on
			// one OS must not silently produce an unresolvable reference on
			// another, so these are rejected portably.
			foreach (var c in path)
			{
				if (c < 0x20 || c == 0x7f || c == '"' || c == '<' || c == '>' || c == '|' || c == '?' || c == '*' || c == ':')
				{
					error = $"{ctx} must be a relative path; the character '{EscapeForMessage(c.ToString())}' " +
							"is not portable in a file path";
					return false;
				}
			}

			// Component-level Windows portability. Windows silently strips
			// trailing periods and spaces from every path component, so a
			// reference whose component ends in '.' or ' ' resolves to a
			// DIFFERENT file on Windows than on POSIX. Windows also reserves
			// device names (CON, PRN, AUX, NUL, COM1..COM9, LPT1..LPT9) in
			// every directory, case-insensitively and with or without an
			// extension. Both make a spec that authors cleanly on one OS
			// unresolvable on another, so they are rejected portably, on
			// EVERY component (backslashes count as separators too: the
			// stored reference is normalized to '/' before anything else
			// compares it).
			foreach (var component in path.Split(new[] { '/', '\\' }, StringSplitOptions.None))
			{
				// "." and ".." are traversal segments, not file names: they
				// are legitimate relative-path syntax on every OS, so they are
				// preserved here. (The editor's path resolution collapses them
				// when storing, so a reference that cannot be stored verbatim
				// is then rejected by the ordered-multiset verification, not
				// silently rewritten.)
				if (component == "." || component == "..")
					continue;
				if (component.EndsWith(" ", StringComparison.Ordinal) || component.EndsWith(".", StringComparison.Ordinal))
				{
					error = $"{ctx} must be a relative path; the component '{EscapeForMessage(component)}' " +
							"ends in a space or period, which Windows cannot represent in a file name";
					return false;
				}
				// Windows' reserved-name rule: strip trailing spaces/dots,
				// then compare the part before the first period,
				// case-insensitively. "con.txt", "CON.", "com1.bin" and
				// "nul" are all reserved; "com10" and "con2" are ordinary
				// file names.
				var baseName = component.TrimEnd(' ', '.').Split('.')[0];
				if (IsWindowsReservedDeviceName(baseName))
				{
					error = $"{ctx} must be a relative path; the component '{EscapeForMessage(component)}' " +
							"is a Windows reserved device name and cannot be stored portably";
					return false;
				}
			}

			normalized = path.Replace('\\', '/');
			return true;
		}

		// The Windows device-name set. Only the exact names are reserved:
		// COM10/LPT10 and beyond are ordinary files, and a prefix such as
		// "con2" or "mycon" is not a device name.
		static bool IsWindowsReservedDeviceName(string baseName)
		{
			switch (baseName.ToUpperInvariant())
			{
				case "CON":
				case "PRN":
				case "AUX":
				case "NUL":
				case "COM1":
				case "COM2":
				case "COM3":
				case "COM4":
				case "COM5":
				case "COM6":
				case "COM7":
				case "COM8":
				case "COM9":
				case "LPT1":
				case "LPT2":
				case "LPT3":
				case "LPT4":
				case "LPT5":
				case "LPT6":
				case "LPT7":
				case "LPT8":
				case "LPT9":
					return true;
				default:
					return false;
			}
		}

		// Control characters are not printable and must never reach the user
		// via an error message; render them as \uXXXX so diagnostics stay
		// readable and machine-parseable.
		static string EscapeForMessage(string s)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var c in s)
			{
				if (c < 0x20 || c == '\u007f')
					sb.Append("\\u").Append(((int)c).ToString("x4"));
				else
					sb.Append(c);
			}
			return sb.ToString();
		}

		static bool ParseChildren(JsonElement el, NodeSpec spec, string ctx, out string error)
		{
			error = "";
			if (el.ValueKind != JsonValueKind.Array)
			{
				error = $"{ctx} must be an array of node specs";
				return false;
			}

			var index = 0;
			foreach (var child in el.EnumerateArray())
			{
				var childSpec = new NodeSpec();
				if (!ParseNode(child, childSpec, allowSettings: false, ctx: $"{ctx}[{index}]", out error))
					return false;
				spec.Children.Add(childSpec);
				index++;
			}
			return true;
		}

		static bool TryInt(JsonElement el, string ctx, int min, int max, out int value, out string error)
		{
			value = 0;
			error = "";
			// TryGetInt32 rejects fractional numbers (1.5), numbers outside the
			// int range, and non-number JSON values: integer semantics are
			// enforced by rejection, never by silent rounding.
			if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out value))
			{
				error = $"{ctx} must be an integer";
				return false;
			}
			if (value < min || value > max)
			{
				error = $"{ctx} must be an integer in [{min}, {max}]";
				return false;
			}
			return true;
		}

		static bool TryColor(JsonElement el, string ctx, double[] color, out string error)
		{
			error = "";
			if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 4)
			{
				error = $"{ctx} must be an array of 4 numbers (normalized RGBA in [0, 1])";
				return false;
			}

			var index = 0;
			foreach (var item in el.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var channel))
				{
					error = $"{ctx}[{index}] must be a number in [0, 1]";
					return false;
				}
				// JsonDocument rejects literal NaN/Infinity tokens, but a number
				// far outside double range can still surface as a non-finite
				// value; reject it rather than converting garbage.
				if (double.IsNaN(channel) || double.IsInfinity(channel) || channel < 0.0 || channel > 1.0)
				{
					error = $"{ctx}[{index}] must be a finite number in [0, 1]";
					return false;
				}
				color[index] = channel;
				index++;
			}
			return true;
		}

		// spawn_count/lifetime: a scalar integer (a fixed value, mapped to
		// SetCenter) or an object {center,min,max} of integers, mapped through
		// SetMin/SetMax/SetCenter. The editor's IntWithRandom setters derive
		// center from min/max (SetMin/SetMax set center = (min+max)/2) and
		// SetCenter keeps a symmetric amplitude around center, so exactly the
		// triples with center == (min+max)/2 are representable. Everything
		// else - including triples the setters would silently narrow - is
		// rejected here with an explicit message.
		static bool TryIntOrRandom(JsonElement el, string ctx, int domainMin, out int center, out int min, out int max, out bool isRange, out string error)
		{
			center = 0;
			min = 0;
			max = 0;
			isRange = false;
			error = "";

			if (el.ValueKind == JsonValueKind.Number)
			{
				return TryInt(el, ctx, domainMin, int.MaxValue, out center, out error);
			}
			if (el.ValueKind != JsonValueKind.Object)
			{
				error = $"{ctx} must be an integer or an object \"center\",\"min\",\"max\"";
				return false;
			}

			isRange = true;
			var hasCenter = false;
			var hasMin = false;
			var hasMax = false;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var prop in el.EnumerateObject())
			{
				if (!seen.Add(prop.Name))
				{
					error = $"{ctx}: duplicate field '{prop.Name}'";
					return false;
				}
				switch (prop.Name)
				{
					case "center":
						if (!TryInt(prop.Value, ctx + ".center", domainMin, int.MaxValue, out center, out error))
							return false;
						hasCenter = true;
						break;
					case "min":
						if (!TryInt(prop.Value, ctx + ".min", domainMin, int.MaxValue, out min, out error))
							return false;
						hasMin = true;
						break;
					case "max":
						if (!TryInt(prop.Value, ctx + ".max", domainMin, int.MaxValue, out max, out error))
							return false;
						hasMax = true;
						break;
					default:
						error = $"{ctx}: unknown field '{prop.Name}'";
						return false;
				}
			}
			if (!hasCenter || !hasMin || !hasMax)
			{
				error = $"{ctx} must contain 'center', 'min' and 'max' (integer values)";
				return false;
			}
			if (min > max)
			{
				error = $"{ctx}: min ({min}) must not exceed max ({max})";
				return false;
			}
			// IntWithRandom's public setters calculate (min + max) in an int;
			// reject sums that would overflow before invoking them.
			var sum = (long)min + max;
			if (sum > int.MaxValue || center != sum / 2)
			{
				error = $"{ctx}: range {{center:{center}, min:{min}, max:{max}}} cannot be represented exactly by the " +
					"editor's random-range setters (they require center == (min + max) / 2 without integer overflow)";
				return false;
			}
			return true;
		}

		static bool TryRendererType(string name, out RendererValues.ParamaterType type)
		{
			switch (name)
			{
				case "sprite": type = RendererValues.ParamaterType.Sprite; return true;
				case "ribbon": type = RendererValues.ParamaterType.Ribbon; return true;
				case "ring": type = RendererValues.ParamaterType.Ring; return true;
				case "model": type = RendererValues.ParamaterType.Model; return true;
				case "track": type = RendererValues.ParamaterType.Track; return true;
				case "none": type = RendererValues.ParamaterType.None; return true;
				default: type = default; return false;
			}
		}

		static string RenderTypeName(RendererValues.ParamaterType type)
		{
			switch (type)
			{
				case RendererValues.ParamaterType.Sprite: return "sprite";
				case RendererValues.ParamaterType.Ribbon: return "ribbon";
				case RendererValues.ParamaterType.Ring: return "ring";
				case RendererValues.ParamaterType.Model: return "model";
				case RendererValues.ParamaterType.Track: return "track";
				case RendererValues.ParamaterType.None: return "none";
				default: return type.ToString();
			}
		}

		static bool ColorSupported(RendererValues.ParamaterType type)
		{
			return type == RendererValues.ParamaterType.Sprite
				|| type == RendererValues.ParamaterType.Model
				|| type == RendererValues.ParamaterType.Ribbon;
		}

		// ---- in-memory authoring --------------------------------------------

		static bool ApplyNode(Node node, NodeSpec spec, out string error)
		{
			error = "";
			if (spec.HasName)
				node.Name.SetValue(spec.Name);

			if (spec.HasMaxGeneration)
				node.CommonValues.MaxGeneration.Value.SetValue(spec.MaxGeneration);
			if (spec.HasSpawnCount)
			{
				if (spec.HasSpawnCountRange)
				{
					// Parse validation guarantees center == (min+max)/2, so this
					// sequence lands exactly on the requested triple: SetMin and
					// SetMax derive center as (min+max)/2, and the SetCenter is
					// then a no-op that still routes the supplied center through
					// its public setter.
					node.CommonValues.Generation.TriggerCount.SetMin(spec.SpawnCountMin);
					node.CommonValues.Generation.TriggerCount.SetMax(spec.SpawnCountMax);
					node.CommonValues.Generation.TriggerCount.SetCenter(spec.SpawnCount);
				}
				else
				{
					node.CommonValues.Generation.TriggerCount.SetCenter(spec.SpawnCount);
				}
			}
			if (spec.HasLifetime)
			{
				if (spec.HasLifetimeRange)
				{
					node.CommonValues.Life.SetMin(spec.LifetimeMin);
					node.CommonValues.Life.SetMax(spec.LifetimeMax);
					node.CommonValues.Life.SetCenter(spec.Lifetime);
				}
				else
				{
					node.CommonValues.Life.SetCenter(spec.Lifetime);
				}
			}

			var type = spec.HasRendererType ? spec.RendererType : RendererValues.ParamaterType.Sprite;
			if (spec.HasRendererType)
				node.DrawingValues.Type.SetValue(type);

			if (spec.HasColor)
			{
				var color = ColorSlot(node, type);
				if (color == null)
				{
					error = $"internal error: no color slot for renderer type {RenderTypeName(type)}";
					return false;
				}
				// Normalized [0,1] -> editor integer channels 0..255, converted
				// deterministically (round half away from zero: 1.0 -> 255,
				// 0.5 -> 128) before the typed Int setters are called.
				color.R.SetValue(ToChannel(spec.Color[0]));
				color.G.SetValue(ToChannel(spec.Color[1]));
				color.B.SetValue(ToChannel(spec.Color[2]));
				color.A.SetValue(ToChannel(spec.Color[3]));
			}

			if (spec.HasColorTexture)
				node.RendererCommonValues.ColorTexture.SetRelativePath(spec.ColorTexture);

			foreach (var childSpec in spec.Children)
			{
				var child = node.AddChild();
				if (!ApplyNode(child, childSpec, out error))
					return false;
			}
			return true;
		}

		// Deterministic normalized -> integer channel conversion: round half
		// away from zero, so 1.0 -> 255, 0.5 -> 128, 0.25 -> 64. The rounding
		// rule is part of the documented spec contract.
		static int ToChannel(double normalized)
		{
			return (int)Math.Round(normalized * 255.0, MidpointRounding.AwayFromZero);
		}

		// The renderer's single "overall color" slot. Sprite and model share
		// the renderer-common StandardColor; ribbon keeps its own all-color
		// fixed slot. Ring and track have no single overall color (multiple
		// per-vertex/per-segment colors), and none has no drawing at all -
		// those combinations are rejected at parse time.
		static Effekseer.Data.Value.Color ColorSlot(Node node, RendererValues.ParamaterType type)
		{
			switch (type)
			{
				case RendererValues.ParamaterType.Sprite:
				case RendererValues.ParamaterType.Model:
					return node.DrawingValues.ColorAll.Fixed;
				case RendererValues.ParamaterType.Ribbon:
					return node.DrawingValues.Ribbon.ColorAll_Fixed;
				default:
					return null;
			}
		}

		// ---- save + verification ---------------------------------------------

		// Transactional save: author into a UNIQUE sibling temp file, verify
		// the temp BEFORE promoting it, then move it over the destination.
		//   - unique + exclusive: a fresh GUID suffix per run, reserved with
		//     FileMode.CreateNew, so concurrent runs and stale temps from
		//     crashed runs can never collide, interleave, or be silently
		//     deleted/reused (a file this run did not create is never
		//     touched);
		//   - verify-before-promote: the temp is validated and its texture
		//     references checked while the destination is still untouched, so
		//     any failure leaves the destination (including a pre-existing
		//     file under --force) exactly as it was;
		//   - force-aware move: with --force the move replaces an existing
		//     destination; without it the move is non-overwriting, closing
		//     the check-then-move race atomically on the same filesystem.
		// The temp lives in the destination directory, so relative resource
		// references serialize and resolve identically against either path.
		// Cleanup is a finally over the whole body: EVERY exit before the
		// promotion deletes this run's own temp (an empty Core.SaveTo
		// result, a failed VerifyOutput, or an exception all return through
		// it), while the promoted path (tmp = null) skips deletion - so a
		// temp this run created can never survive a failed run, and a stale
		// temp from another run (a different name) is never touched.
		// Returns 0 (ok), 2 (valid with warnings) or 1 (failure, error set).
		static int SaveTransactional(string outputPath, NodeSpec spec, bool force, out string error)
		{
			error = null;
			var full = Path.GetFullPath(outputPath);
			var directory = Path.GetDirectoryName(full);
			string tmp = null;
			try
			{
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				tmp = Path.Combine(
					string.IsNullOrEmpty(directory) ? "." : directory,
					"." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
				using (new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

				Core.SaveTo(tmp);

				if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
				{
					error = $"could not write {outputPath}";
					return 1;
				}

				// Verify the TEMP file, reporting issues under the destination
				// name. Nothing has touched the destination yet.
				var status = VerifyOutput(tmp, full, spec);
				if (status == 1)
				{
					error = $"from-spec output failed validation: {full}";
					return 1;
				}

				File.Move(tmp, full, overwrite: force);
				tmp = null; // promoted; nothing left to clean up

				Console.WriteLine($"saved {full}");
				return status == 2 ? 2 : 0;
			}
			catch (Exception ex)
			{
				if (Directory.Exists(full))
					error = $"cannot write {outputPath}: the path exists and is not a file";
				else
					error = $"save failed: {ex.GetType().Name}: {ex.Message}";
				return 1;
			}
			finally
			{
				// Runs on every exit - the two failure returns above, the
				// success return, and any exception. Only the promoted path
				// (tmp == null) skips the delete, so this run's unique temp
				// cannot survive a failed run, and a stale temp from another
				// run (a different name) is never deleted.
				if (tmp != null)
				{
					try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
				}
			}
		}

		// Reload the candidate file and validate it the same way 'efkc
		// validate' does (structural + schema): zero errors is the success
		// contract. Also confirm every authored color_texture is stored as the
		// same relative reference, compared as an ORDERED MULTISET: each spec
		// texture must appear exactly as often as the spec lists it, in the
		// same order, with no extras - the regression guard for the
		// root-path-before-SetRelativePath ordering and for duplication /
		// omission drift. The reload and multiset check ALWAYS run, even when
		// the spec lists no textures: the empty case must still prove the
		// output carries no non-empty reference (skipping it would let a
		// corrupt or texture-bearing file pass a no-texture spec). verifyPath
		// is the on-disk candidate (the temp during a transactional save);
		// displayPath is what reports name (the destination). Returns 0 (ok),
		// 2 (warnings) or 1 (error).
		static int VerifyOutput(string verifyPath, string displayPath, NodeSpec spec)
		{
			var schema = Schema.Generate();
			var issues = Validator.Run(verifyPath, new Validator.Options { }, schema);
			CliOutput.EmitHumanFile(new ValidationRunner.FileResult
			{
				Path = displayPath,
				Issues = issues,
				Status = CliOutput.StatusFor(issues, false),
			});
			var status = CliOutput.StatusFor(issues, false);
			if (status == "error")
			{
				return 1;
			}

			var expected = new List<string>();
			CollectColorTextures(spec, expected);

			if (!EffekseerLoad.TryLoad(verifyPath, out _, out var root, out var loadIssues))
			{
				foreach (var issue in loadIssues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"efkc: reload of from-spec output failed: {issue.Message}");
				return 1;
			}

			if (root == null)
			{
				Console.Error.WriteLine("efkc: reload of from-spec output produced no tree");
				return 1;
			}

			var actual = EffectEdit.WalkResources(root).Resources
				.Where(r => !string.IsNullOrEmpty(r.Relative))
				.Select(r => r.Relative.Replace('\\', '/'))
				.ToList();

			// Each authored node contributes at most one texture (its
			// color_texture) and children are walked in document order
			// after their parent, so the walk list equals the spec's list
			// exactly when nothing was lost, reordered, duplicated or
			// invented. SequenceEqual is the ordered-multiset comparison;
			// with an empty spec list it must match an equally empty walk.
			if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
			{
				for (int i = 0; i < Math.Max(actual.Count, expected.Count); i++)
				{
					var stored = i < actual.Count ? actual[i] : "<missing>";
					var wanted = i < expected.Count ? expected[i] : "<extra>";
					if (!string.Equals(stored, wanted, StringComparison.Ordinal))
					{
						Console.Error.WriteLine(
							$"efkc: from-spec texture reference #{i}: stored '{stored}' but spec requires '{wanted}'");
					}
				}
				return 1;
			}

			return status == "warning" ? 2 : 0;
		}

		static void CollectColorTextures(NodeSpec spec, List<string> into)
		{
			if (spec.HasColorTexture)
				into.Add(spec.ColorTexture.Replace('\\', '/'));
			foreach (var child in spec.Children)
				CollectColorTextures(child, into);
		}
	}
}
// [UAA] - END
