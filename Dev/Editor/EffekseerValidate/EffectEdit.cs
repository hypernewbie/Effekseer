// [UAA] - START - headless effect resource editing and runtime export
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
		// rather than just a list of bare strings.
		public sealed class Resource
		{
			public Effekseer.Data.Value.Path Value;
			public string Kind;
			public string Owner;

			public string Relative => Value.GetRelativePath();
			public string Absolute => Value.GetAbsolutePath();
		}

		static bool IsEffekseerData(Type t)
		{
			return t.Namespace == "Effekseer.Data" || t.Namespace == "Effekseer.Data.Value" ||
				   (t.Namespace != null && t.Namespace.StartsWith("Effekseer.Data.", StringComparison.Ordinal));
		}

		/// <summary>
		/// Collects every resource reference in the effect tree.
		/// </summary>
		public static List<Resource> CollectResources(NodeRoot root)
		{
			var found = new List<Resource>();
			var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			Walk(root, root.GetType().Name, seen, found);
			return found;
		}

		static void Walk(object obj, string owner, HashSet<object> seen, List<Resource> found)
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
				found.Add(new Resource
				{
					Value = path,
					Kind = type.Name,
					Owner = owner,
				});
				return;
			}

			// Children collections and any other sequence of data objects.
			if (obj is IEnumerable sequence)
			{
				foreach (var item in sequence)
					Walk(item, owner, seen, found);
				return;
			}

			// Stay inside the effect data model; do not wander into framework types.
			if (!IsEffekseerData(type))
				return;

			var nextOwner = obj is NodeBase node ? DescribeNode(node) : owner;

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
					// read outside the GUI. They cannot hold resources we need.
					continue;
				}

				Walk(value, nextOwner, seen, found);
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
					continue;
				}

				Walk(value, nextOwner, seen, found);
			}
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

		/// <summary>
		/// Rewrites resource references whose relative path starts with
		/// <paramref name="from"/> so it starts with <paramref name="to"/> instead.
		/// Matching happens on the relative form, which is what the file stores and
		/// what a user sees listed. Returns the number of references rewritten.
		/// </summary>
		public static int Retarget(NodeRoot root, string from, string to, bool dryRun, TextWriter log)
		{
			var rewritten = 0;

			foreach (var resource in CollectResources(root))
			{
				var relative = resource.Relative;
				if (string.IsNullOrEmpty(relative) || !relative.StartsWith(from, StringComparison.Ordinal))
					continue;

				var updated = to + relative.Substring(from.Length);
				log?.WriteLine($"  {resource.Kind} [{resource.Owner}]: {relative} -> {updated}");

				if (!dryRun)
					resource.Value.SetRelativePath(updated);

				rewritten++;
			}

			return rewritten;
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
}
// [UAA] - END
