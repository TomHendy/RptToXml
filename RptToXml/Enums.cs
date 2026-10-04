using System;

namespace RptToXml
{
	[Flags]
	public enum FormatTypes
	{
		None = 0,
		Border = 1 << 0,
		Color = 1 << 1,
		Font = 1 << 2,
		AreaFormat = 1 << 3,
		FieldFormat = 1 << 4,
		ObjectFormat = 1 << 5,
		SectionFormat = 1 << 6,
		All = Border | Color | Font | AreaFormat | FieldFormat | ObjectFormat | SectionFormat
	}

	[Flags]
	public enum ObjectTypes
	{
		None = 0,
		Area = 1 << 0,
		Section = 1 << 1,
		ReportObject = 1 << 2,
		All = Area | Section | ReportObject
	}
}