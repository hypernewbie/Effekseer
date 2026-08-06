// [UAA] - START - headless effect resource editing and runtime export
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Effekseer;
using Effekseer.Data;

namespace EffekseerValidate
{
	// Headless editing of effect files.
	//
	// Effect authoring lives entirely in this C# core: the runtime has no effect
	// writer at all, so anything that rewrites an .efkefc or cooks an .efk has to
	// happen here rather than in the C++ material CLI.
	//
	// Every resource reference in the tree (image, model, sound, material, curve)
	// derives from Effekseer.Data.Value.Path, so one reflection walk finds them
	// all and resource retargeting stays type-safe.
	public static class EffectEdit
	{
		// A resource reference plus where it was found, so output can be useful
		// rather than just a list of bare strings. Location is a stable dotted
		// property path from the root, e.g. "Spawn.RendererCommonValues.ColorTexture".
		public sealed class Resource
		{
			public Effekseer.Data.Value.Path Value;
			public string Kind;
			public string Location;

			public string Relative => Value.GetRelativePath();
			public string Absolute => Value.GetAbsolutePath();
			public bool Exists => !string.IsNullOrEmpty(Absolute) && File.Exists(Absolute);
		}

		// Walk result: the ordered resource inventory plus walker blind spots.
		// A non-empty Warnings list means the inventory cannot promise
		// completeness (some reflection getter could not be read).
		public sealed class WalkResult
		{
			public List<Resource> Resources = new List<Resource>();
			public List<string> Warnings = new List<string>();
		}

		public sealed class RetargetChange
		{
			public string Kind;
			public string Location;
			public string OldRelative;
			public string NewRelative;
			public string OldAbsolute;
			public string NewAbsolute;
		}

		static bool IsEffekseerData(Type t)
		{
			return t.Namespace == "Effekseer.Data" || t.Namespace == "Effekseer.Data.Value" ||
				   (t.Namespace != null && t.Namespace.StartsWith("Effekseer.Data.", StringComparison.Ordinal));
		}

		/// <summary>
		/// Collects every resource reference in the effect tree, in walk order.
		/// </summary>
		public static WalkResult WalkResources(NodeRoot root)
		{
			var result = new WalkResult();
			var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			Walk(root, "Root", seen, result);
			return result;
		}

		static void Walk(object obj, string location, HashSet<object> seen, WalkResult result)
		{
			if (obj == null)
				return;

			if (obj is string)
				return;

			var type = obj.GetType();
			if (type.IsPrimitive || type.IsEnum)
				return;

			if (!seen.Add(obj))
				return;

			if (obj is Effekseer.Data.Value.Path path)
			{
				result.Resources.Add(new Resource
				{
					Value = path,
					Kind = type.Name,
					Location = location,
				});
				return;
			}

			// Children collections and any other sequence of data objects.
			if (obj is NodeBase.ChildrenCollection children)
			{
				// ChildrenCollection exposes its items via .Internal rather than
				// implementing IEnumerable. Walk them as "location[i].Name" so
				// locations read "Root.Children[0].Laser..." rather than
				// "Root.Children.Internal[0]...".
				var index = 0;
				foreach (var item in children.Internal)
				{
					Walk(item, location + "[" + index + "]." + DescribeNode(item), seen, result);
					index++;
				}
				return;
			}

			if (obj is IEnumerable sequence)
			{
				var index = 0;
				foreach (var item in sequence)
				{
					// Non-node items use an index suffix alone; nodes reached here
					// (e.g. inside Value.ObjectCollection) keep index + name.
					var itemLocation = item is NodeBase node
						? location + "[" + index + "]." + DescribeNode(node)
						: location + "[" + index + "]";
					Walk(item, itemLocation, seen, result);
					index++;
				}
				return;
			}

			// Stay inside the effect data model; do not wander into framework types.
			if (!IsEffekseerData(type))
				return;

			foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (property.GetIndexParameters().Length != 0 || !property.CanRead)
					continue;

				object value;
				try
				{
					value = property.GetValue(obj);
				}
				catch (Exception)
				{
					// Some properties are editor-context dependent and throw when
					// read outside the GUI. Only properties that could hold a
					// resource threaten inventory completeness, so those are the
					// ones worth reporting as walker blind spots.
					if (CouldHoldResources(property.PropertyType))
						result.Warnings.Add(
							$"could not read {location}.{property.Name} ({property.PropertyType.Name}); inventory may be incomplete");
					continue;
				}

				Walk(value, location + "." + property.Name, seen, result);
			}

			foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				object value;
				try
				{
					value = field.GetValue(obj);
				}
				catch (Exception)
				{
					if (CouldHoldResources(field.FieldType))
						result.Warnings.Add(
							$"could not read {location}.{field.Name} ({field.FieldType.Name}); inventory may be incomplete");
					continue;
				}

				Walk(value, location + "." + field.Name, seen, result);
			}
		}

		// True if an unreadable getter of this type could hide a resource.
		// Primitives/strings cannot, so failures on them are skipped silently.
		static bool CouldHoldResources(Type t)
		{
			if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
				return false;
			if (typeof(Effekseer.Data.Value.Path).IsAssignableFrom(t))
				return true;
			if (t.Namespace != null && t.Namespace.StartsWith("Effekseer.Data", StringComparison.Ordinal))
				return true;
			return typeof(IEnumerable).IsAssignableFrom(t);
		}

		static string DescribeNode(NodeBase node)
		{
			try
			{
				var name = node.Name?.Value;
				return string.IsNullOrEmpty(name) ? node.GetType().Name : name;
			}
			catch (Exception)
			{
				return node.GetType().Name;
			}
		}

		internal static List<string> PathSegments(string path)
		{
			return path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
		}

		// Segment-wise prefix match: "Textures" matches "Textures/a.png" but
		// not "TexturesOld/a.png", and "Textures/particle" matches
		// "Textures/particle/x.png" but not "Textures/old/x.png".
		internal static bool StartsWithSegments(List<string> segments, List<string> prefix)
		{
			if (segments.Count < prefix.Count)
				return false;
			for (var i = 0; i < prefix.Count; i++)
			{
				if (!string.Equals(segments[i], prefix[i], StringComparison.Ordinal))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Rewrites resource references whose relative path starts with
		/// <paramref name="from"/> (segment-aware) so it starts with
		/// <paramref name="to"/> instead. Matching happens on the relative form,
		/// which is what the file stores and what a user sees listed. Returns the
		/// changes; each records the old and new relative and absolute paths so
		/// callers can verify a save+reload round-trip landed.
		/// </summary>
		public static List<RetargetChange> Retarget(NodeRoot root, string from, string to, bool dryRun, TextWriter log)
		{
			var changes = new List<RetargetChange>();
			var fromSegments = PathSegments(from);
			var toSegments = PathSegments(to);

			foreach (var resource in WalkResources(root).Resources)
			{
				var relative = resource.Relative;
				if (string.IsNullOrEmpty(relative))
					continue;

				var relSegments = PathSegments(relative);
				if (!StartsWithSegments(relSegments, fromSegments))
					continue;

				var updated = string.Join("/", toSegments.Concat(relSegments.Skip(fromSegments.Count)));
				var oldAbsolute = resource.Absolute;
				log?.WriteLine($"  {resource.Kind} [{resource.Location}]: {relative} -> {updated}");

				if (!dryRun)
					resource.Value.SetRelativePath(updated);

				changes.Add(new RetargetChange
				{
					Kind = resource.Kind,
					Location = resource.Location,
					OldRelative = relative,
					NewRelative = updated,
					OldAbsolute = oldAbsolute,
					NewAbsolute = resource.Absolute,
				});
			}

			return changes;
		}

		/// <summary>
		/// Cooks the runtime binary an application actually loads.
		/// </summary>
		public static bool ExportEfk(NodeRoot root, string outputPath, float magnification, out string error)
		{
			error = null;

			byte[] data;
			try
			{
				var exporter = new Effekseer.Binary.Exporter();
				data = exporter.Export(root, magnification);
			}
			catch (Exception ex)
			{
				error = $"export failed: {ex.GetType().Name}: {ex.Message}";
				return false;
			}

			if (data == null || data.Length == 0)
			{
				error = "export produced no data";
				return false;
			}

			return WriteAllBytes(outputPath, data, out error);
		}

		/// <summary>
		/// Saves the editor-side container.
		/// </summary>
		/// <remarks>
		/// Goes through Core.SaveTo rather than Effekseer.IO.EfkEfc, which is
		/// internal to EffekseerCore. Core.SaveTo writes the global Core.Root, which
		/// is exactly the tree Core.LoadFrom produced and this tool then edits, so
		/// the indirection costs nothing and keeps the core's visibility untouched.
		/// The identity is asserted rather than assumed.
		/// </remarks>
		public static bool SaveEfkEfc(NodeRoot root, string outputPath, out string error)
		{
			error = null;

			if (!ReferenceEquals(root, Core.Root))
			{
				error = "internal error: the edited tree is not the core's loaded root, so saving " +
						"would write the wrong effect";
				return false;
			}

			try
			{
				var full = System.IO.Path.GetFullPath(outputPath);
				EnsureDirectory(full);

				// Core.SaveTo calls Root.SetFullPath itself, so resource references are
				// re-relativized against the destination.
				Core.SaveTo(full);

				if (!File.Exists(full) || new FileInfo(full).Length == 0)
				{
					error = $"could not write {outputPath}";
					return false;
				}
			}
			catch (Exception ex)
			{
				error = $"save failed: {ex.GetType().Name}: {ex.Message}";
				return false;
			}

			return true;
		}

		static bool WriteAllBytes(string outputPath, byte[] data, out string error)
		{
			error = null;
			try
			{
				var full = System.IO.Path.GetFullPath(outputPath);
				EnsureDirectory(full);
				File.WriteAllBytes(full, data);
			}
			catch (Exception ex)
			{
				error = $"could not write {outputPath}: {ex.GetType().Name}: {ex.Message}";
				return false;
			}

			return true;
		}

		static void EnsureDirectory(string fullPath)
		{
			var directory = System.IO.Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
		}
	}

	// validate --check-resources: turns resource-walker findings into
	// validation issues. Missing files are errors (the corpus gate); walker
	// blind spots (unreadable reflection getters) are warnings.
	public static class ResourceCheck
	{
		public static List<Issue> Run(string path, NodeRoot root)
		{
			var issues = new List<Issue>();
			var walk = EffectEdit.WalkResources(root);

			foreach (var r in walk.Resources)
			{
				if (string.IsNullOrEmpty(r.Relative))
					continue;
				if (!r.Exists)
					issues.Add(Issue.Error(path, 0, 0,
						$"resource not found: {r.Relative} (from {r.Location})"));
			}

			foreach (var w in walk.Warnings)
				issues.Add(Issue.Warning(path, 0, 0, $"resource walker: {w}"));

			return issues;
		}
	}
}
// [UAA] - END
