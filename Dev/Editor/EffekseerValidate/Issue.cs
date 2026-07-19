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

		public Issue(Severity severity, string path, int line, int column, string message)
		{
			Severity = severity;
			Path = path;
			Line = line;
			Column = column;
			Message = message;
		}

		public static Issue Error(string path, int line, int column, string message)
			=> new Issue(Severity.Error, path, line, column, message);

		public static Issue Warning(string path, int line, int column, string message)
			=> new Issue(Severity.Warning, path, line, column, message);

		public static Issue FromXmlException(string path, XmlException ex)
			=> new Issue(Severity.Error, path, ex.LineNumber, ex.LinePosition,
				$"XML parse error: {ex.Message}");
	}
}