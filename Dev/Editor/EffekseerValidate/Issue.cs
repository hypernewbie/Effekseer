using System.Xml;

namespace EffekseerValidate
{
	public enum Severity
	{
		Error,
		Warning,
	}

	public class Issue
	{
		public Severity Severity { get; }
		public string Path { get; }
		public int Line { get; }
		public int Column { get; }
		public string Message { get; }

		// [UAA] - START - efkc: add stable machine-readable issue categories
		// Most issues are unclassified; resource checks standardize
		// resource_missing and resource_walker_blind_spot so callers never parse
		// message text.
		public string Code { get; }

		// Preserve the original CLR constructor and factory signatures for
		// already-compiled callers. The coded overloads are additive.
		public Issue(Severity severity, string path, int line, int column, string message)
			: this(severity, path, line, column, message, "unclassified")
		{
		}

		public Issue(Severity severity, string path, int line, int column, string message, string code)
		{
			Severity = severity;
			Path = path;
			Line = line;
			Column = column;
			Message = message;
			Code = code;
		}

		public static Issue Error(string path, int line, int column, string message)
			=> new Issue(Severity.Error, path, line, column, message);

		public static Issue Error(string path, int line, int column, string message, string code)
			=> new Issue(Severity.Error, path, line, column, message, code);

		public static Issue Warning(string path, int line, int column, string message)
			=> new Issue(Severity.Warning, path, line, column, message);

		public static Issue Warning(string path, int line, int column, string message, string code)
			=> new Issue(Severity.Warning, path, line, column, message, code);
		// [UAA] - END

		public static Issue FromXmlException(string path, XmlException ex)
			=> new Issue(Severity.Error, path, ex.LineNumber, ex.LinePosition,
				$"XML parse error: {ex.Message}");
	}
}