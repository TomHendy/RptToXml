using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Runtime.ExceptionServices;

using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.Shared;

using CRDataDefModel = CrystalDecisions.ReportAppServer.DataDefModel;
using CRReportDefModel = CrystalDecisions.ReportAppServer.ReportDefModel;

using OpenMcdf;

namespace RptToXml
{
	public partial class RptDefinitionWriter : IDisposable
	{
		// FormatTypes used to be declared with '^' (XOR) instead of powers of two, which made every
		// (ShowFormatTypes & X) == X check true; All keeps the output that produced.
		private const FormatTypes ShowFormatTypes = FormatTypes.All;

		private ReportDocument _report;
		private ISCDReportClientDocument _rcd;
		private CompoundFile _oleCompoundFile;

		private readonly bool _createdReport;
		private readonly bool _stdOut;

		public RptDefinitionWriter(string filename, bool stdOut)
		{
			_stdOut = stdOut;
			_createdReport = true;
			_report = new ReportDocument();
			_report.Load(filename, OpenReportMethod.OpenReportByTempCopy);
			_rcd = _report.ReportClientDocument;

			_oleCompoundFile = new CompoundFile(filename);

			if (!stdOut)
            {
                Trace.WriteLine("Loaded report");
            }
        }

		public RptDefinitionWriter(ReportDocument value)
		{
			_report = value;
			_rcd = _report.ReportClientDocument;
		}

		public void WriteToXml()
		{
			// Buffer the whole document so a failure part-way through writes nothing to stdout, then copy the
			// UTF-8 bytes (no BOM, matching the encoding="utf-8" declaration) to the raw standard output stream,
			// bypassing the console code page (this is the git textconv path).
			using (var buffer = new System.IO.MemoryStream())
			{
				WriteToXml(buffer, new UTF8Encoding(false));

				System.IO.Stream stdout = Console.OpenStandardOutput();
				buffer.WriteTo(stdout);
				stdout.Flush();
			}
		}

        public void WriteToXml(string targetXmlPath)
        {
            using (System.IO.Stream output = System.IO.File.Create(targetXmlPath))
            {
                WriteToXml(output);
            }
        }

		public void WriteToXml(System.IO.Stream output)
		{
			WriteToXml(output, Encoding.UTF8);
		}

		private void WriteToXml(System.IO.Stream output, Encoding encoding)
		{

			XmlWriterSettings settings = new XmlWriterSettings
            {
                CheckCharacters = true,
                Encoding = encoding,
                Indent = true
            };
            using (XmlWriter writer = XmlWriter.Create(output, settings))
			{
				WriteToXml(writer);
			}
		}

		public void WriteToXml(XmlWriter writer)
		{
			if (!_stdOut)
            {
                Trace.WriteLine("Writing to XML");
            }

            writer.WriteStartDocument();
			ProcessReport(_report, writer);
			writer.WriteEndDocument();
			writer.Flush();
		}

		// Matches characters that are not legal in XML 1.0: unpaired surrogates and anything outside
		// #x9 | #xA | #xD | [#x20-#xFFFD]. Well-formed surrogate pairs (#x10000-#x10FFFF) are kept.
		private static readonly System.Text.RegularExpressions.Regex CompiledRegexp =
			new System.Text.RegularExpressions.Regex("[\\uD800-\\uDBFF](?![\\uDC00-\\uDFFF])|(?<![\\uD800-\\uDBFF])[\\uDC00-\\uDFFF]|[^\\u0009\\u000a\\u000d\\u0020-\\uFFFD]",
				System.Text.RegularExpressions.RegexOptions.Compiled);

		private static void WriteAttributeString(XmlWriter writer, string name, string value)
		{
			string myValue = value == null ? null : CompiledRegexp.Replace(value, "");
			writer.WriteAttributeString(name, myValue ?? "");
		}
		private static void WriteString(XmlWriter writer, string value)
		{
			string myValue = value == null ? null : CompiledRegexp.Replace(value, "");
			writer.WriteString(myValue ?? "");
		}
		private static void WriteElementString(XmlWriter writer, string localName, string value)
		{
			// same shape as XmlWriter.WriteElementString (empty element for null/""), but sanitized
			writer.WriteStartElement(localName);
			if (!String.IsNullOrEmpty(value))
			{
				WriteString(writer, value);
			}
			writer.WriteEndElement();
		}

		// Writes one element subtree into a detached buffer and copies it to writer only if write completes, so an exception
		// part-way through cannot leave elements open in the main document.
		// XElement.WriteTo replays the buffered calls through writer, so its settings (indentation, escaping, newlines, character
		// checks) apply and the bytes match a direct write, provided no element mixes text with child elements or splits its
		// text over several WriteString calls (the buffer merges adjacent text and drops empty text beside child elements);
		// nothing in this class does either.
		private static void WriteBuffered(XmlWriter writer, Action<XmlWriter> write)
		{
			var buffer = new System.Xml.Linq.XDocument();
			using (XmlWriter bufferWriter = buffer.CreateWriter()) // the written nodes reach buffer when bufferWriter is closed
			{
				write(bufferWriter);
			}
			buffer.Root?.WriteTo(writer);
		}

		// Reads a name for an error message or marker element; the object that just failed may fail again here, including with
		// the corrupted-state exception (such as an access violation) that GetSubreports catches, hence the attribute.
		[HandleProcessCorruptedStateExceptions]
		private static string TryGetName(Func<string> getName)
		{
			try
			{
				return getName();
			}
			catch (Exception)
			{
				return null;
			}
		}

		// Writes one element subtree for data that earlier versions of this tool did not dump. If reading it fails, the subtree
		// is left out and the failure reported, so the rest of the report is still dumped as it was before the subtree existed.
		[HandleProcessCorruptedStateExceptions]
		private static void WriteOptional(XmlWriter writer, string description, Action<XmlWriter> write)
		{
			try
			{
				WriteBuffered(writer, write);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error reading {description}, {e.Message}");
			}
		}

		// Writes an attribute whose value earlier versions of this tool did not read from the report. If reading it fails,
		// the failure is reported and the attribute left out, so the rest of the element is still written as before.
		[HandleProcessCorruptedStateExceptions]
		private static void WriteOptionalAttribute(XmlWriter writer, string name, string description, Func<string> read)
		{
			string value;
			try
			{
				value = read();
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error reading {description}, {e.Message}");
				return;
			}

			if (value != null)
			{
				WriteAttributeString(writer, name, value);
			}
		}

        //This is a recursive method.  GetSubreports() calls it.
		private void ProcessReport(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Report");

			WriteAttributeString(writer, "Name", report.Name);
			if (!_stdOut)
            {
                Trace.WriteLine("Writing report " + report.Name);
            }

            if (!report.IsSubreport)
			{
				if (!_stdOut)
                {
                    Trace.WriteLine("Writing header info");
                }

                // left out with --stdout (the git textconv path): git passes a temporary copy, so the name would change in every diff
                if (!_stdOut)
                {
                    WriteAttributeString(writer, "FileName", report.FileName.Replace("rassdk://", ""));
                }
				WriteAttributeString(writer, "HasSavedData", report.HasSavedData.ToString());

				if (_oleCompoundFile != null)
				{
					writer.WriteStartElement("Embedinfo");
					_oleCompoundFile.RootStorage.VisitEntries(fileItem =>
					{
						if (fileItem.Name.Contains("Ole"))
						{
							writer.WriteStartElement("Embed");
							WriteAttributeString(writer, "Name", fileItem.Name);

							var cfStream = fileItem as CFStream;
							if (cfStream != null)
							{
								var streamBytes = cfStream.GetData();

								WriteAttributeString(writer, "Size", cfStream.Size.ToString("0"));

								using (var md5Provider = new MD5CryptoServiceProvider())
								{
									byte[] md5Hash = md5Provider.ComputeHash(streamBytes);
									WriteAttributeString(writer, "MD5Hash", Convert.ToBase64String(md5Hash));
								}
							}
							writer.WriteEndElement();
						}
					}, true);
					writer.WriteEndElement();
				}

				GetSummaryinfo(report, writer);
				GetReportOptions(report, writer);
				GetPrintOptions(report, writer);
				GetSubreports(report, writer);  //recursion happens here.
			}

			GetDatabase(report, writer);
			GetDataDefinition(report, writer);
			GetCustomFunctions(report, writer);
			GetSubReportsLinks(report, writer);
			GetReportDefinition(report, writer);

			writer.WriteEndElement();
		}

		private static void GetSummaryinfo(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Summaryinfo");

			WriteAttributeString(writer, "KeywordsinReport", report.SummaryInfo.KeywordsInReport);
			WriteAttributeString(writer, "ReportAuthor", report.SummaryInfo.ReportAuthor);
			WriteAttributeString(writer, "ReportComments", report.SummaryInfo.ReportComments);
			WriteAttributeString(writer, "ReportSubject", report.SummaryInfo.ReportSubject);
			WriteAttributeString(writer, "ReportTitle", report.SummaryInfo.ReportTitle);

			writer.WriteEndElement();
		}

		private static void GetReportOptions(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("ReportOptions");

			WriteAttributeString(writer, "EnableSaveDataWithReport", report.ReportOptions.EnableSaveDataWithReport.ToString());
			WriteAttributeString(writer, "EnableSavePreviewPicture", report.ReportOptions.EnableSavePreviewPicture.ToString());
			WriteAttributeString(writer, "EnableSaveSummariesWithReport", report.ReportOptions.EnableSaveSummariesWithReport.ToString());
			WriteAttributeString(writer, "EnableUseDummyData", report.ReportOptions.EnableUseDummyData.ToString());
			WriteAttributeString(writer, "initialDataContext", report.ReportOptions.InitialDataContext);
			WriteAttributeString(writer, "initialReportPartName", report.ReportOptions.InitialReportPartName);

			writer.WriteEndElement();
		}

		private void GetPrintOptions(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("PrintOptions");

			WriteAttributeString(writer, "PageContentHeight", report.PrintOptions.PageContentHeight.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "PageContentWidth", report.PrintOptions.PageContentWidth.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "PaperOrientation", report.PrintOptions.PaperOrientation.ToString());
			WriteAttributeString(writer, "PaperSize", report.PrintOptions.PaperSize.ToString());
			WriteAttributeString(writer, "PaperSource", report.PrintOptions.PaperSource.ToString());
			WriteAttributeString(writer, "PrinterDuplex", report.PrintOptions.PrinterDuplex.ToString());
			WriteAttributeString(writer, "PrinterName", report.PrintOptions.PrinterName);

			writer.WriteStartElement("PageMargins");

			WriteAttributeString(writer, "bottomMargin", report.PrintOptions.PageMargins.bottomMargin.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "leftMargin", report.PrintOptions.PageMargins.leftMargin.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "rightMargin", report.PrintOptions.PageMargins.rightMargin.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "topMargin", report.PrintOptions.PageMargins.topMargin.ToString(CultureInfo.InvariantCulture));

			writer.WriteEndElement();

			CRReportDefModel.PrintOptions rdmPrintOptions = GetRASRDMPrintOptionsObject(report.Name, report);
			if (rdmPrintOptions != null)
            {
                GetPageMarginConditionFormulas(rdmPrintOptions, writer);
            }

            writer.WriteEndElement();
		}

		private void GetSubReportsLinks(ReportDocument report, XmlWriter writer)
		{
			if (report.IsSubreport)
			{
				writer.WriteStartElement("SubReportLinks");
				CRReportDefModel.SubreportLinks subReportLinks = _report.ReportClientDocument.SubreportController.GetSubreportLinks(report.Name);

				if (subReportLinks != null)
                {
                    foreach (CRReportDefModel.SubreportLink link in subReportLinks)
                    {
                        writer.WriteStartElement("SubReportLink");
                        WriteAttributeString(writer, "LinkedParameterName", link.LinkedParameterName);
                        WriteAttributeString(writer, "MainReportFieldName", link.MainReportFieldName);
                        WriteAttributeString(writer, "SubreportFieldName", link.SubreportFieldName);
                        writer.WriteEndElement();
                    }
                }

                writer.WriteEndElement();
			}
		}

		[HandleProcessCorruptedStateExceptions]
		private void GetSubreports(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("SubReports");

			// The outer try covers enumerating report.Subreports, which can itself throw (issue #47). Each subreport is written
			// through WriteBuffered, so one that fails part-way through is replaced by a marker element instead of leaving its
			// elements open, and the remaining subreports are still written.
			try
			{
				foreach (ReportDocument subreport in report.Subreports)
				{
					try
					{
						WriteBuffered(writer, w => ProcessReport(subreport, w));
					}
					catch (Exception e)
					{
						string name = TryGetName(() => subreport.Name);
						Console.Error.WriteLine($"Error processing subreport '{name}', {e}");
						writer.WriteStartElement("Report");
						WriteAttributeString(writer, "Name", name);
						WriteAttributeString(writer, "Error", e.GetType().Name); // not the message, which can name a temporary file
						writer.WriteEndElement();
					}
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error loading subreport, {e}");
			}
			writer.WriteEndElement();
		}

		private void GetDatabase(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Database");

			GetTableLinks(report, writer);
			if (!report.IsSubreport)
			{
				var reportClientDocument = report.ReportClientDocument;
				GetReportClientTables(reportClientDocument, writer);
			}
			else
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				GetSubreportClientTables(subrptClientDoc, writer);
			}

			writer.WriteEndElement();
		}

		private static void GetTableLinks(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("TableLinks");

			foreach (TableLink tl in report.Database.Links)
			{
				writer.WriteStartElement("TableLink");
				WriteAttributeString(writer, "JoinType", tl.JoinType.ToString());

				writer.WriteStartElement("SourceFields");
				foreach (FieldDefinition fd in tl.SourceFields)
                {
                    GetFieldDefinition(fd, writer);
                }

                writer.WriteEndElement();

				writer.WriteStartElement("DestinationFields");
				foreach (FieldDefinition fd in tl.DestinationFields)
                {
                    GetFieldDefinition(fd, writer);
                }

                writer.WriteEndElement();

				writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

		private void GetCustomFunctions(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("CustomFunctions");

            // Custom functions can only be read for the main report. The sole RAS accessor is
            // ISCDReportClientDocument.CustomFunctionController.GetCustomFunctions(), which takes no
            // subreport name. For a subreport, report.ReportClientDocument is the engine's SubreportWrapper,
            // whose CustomFunctionController getter throws NotSupportedException ("not supported by
            // subreport"); the RAS document from SubreportController.GetSubreport(name)
            // (ISCRSubreportClientDocument) has no CustomFunctionController, and neither its Document
            // (ReportDefModel.ReportDocument) nor DataDefModel.DataDefinition exposes custom functions.
            // So a subreport always gets an empty <CustomFunctions/> (kept for schema stability), which
            // does not mean the subreport defines none.
            // RAS CustomFunction exposes only Name, Syntax and Text (plus ClassName, the RAS object type name);
            // return type and arguments are part of Text, while summary/category/author are not exposed.
            CRDataDefModel.CustomFunctions funcs = !report.IsSubreport
                ? report.ReportClientDocument.CustomFunctionController.GetCustomFunctions()
                : null;

			if (funcs != null)
			{
				foreach (CRDataDefModel.CustomFunction func in funcs)
				{
					writer.WriteStartElement("CustomFunction");
					WriteAttributeString(writer, "Name", func.Name);
					WriteAttributeString(writer, "Syntax", func.Syntax.ToString());
					WriteElementString(writer, "Text", func.Text); // an element so line breaks are literal

					writer.WriteEndElement();
				}
			}

			writer.WriteEndElement();
		}

		private void GetReportClientTables(ISCDReportClientDocument reportClientDocument, XmlWriter writer)
		{
			writer.WriteStartElement("Tables");

			foreach (CrystalDecisions.ReportAppServer.DataDefModel.Table table in reportClientDocument.DatabaseController.Database.Tables)
			{
				GetTable(table, writer);
			}

			writer.WriteEndElement();
		}
		private void GetSubreportClientTables(SubreportClientDocument subrptClientDocument, XmlWriter writer)
		{
			writer.WriteStartElement("Tables");

			foreach (CrystalDecisions.ReportAppServer.DataDefModel.Table table in subrptClientDocument.DatabaseController.Database.Tables)
			{
				GetTable(table, writer);
			}

			writer.WriteEndElement();
		}

		private void GetTable(CrystalDecisions.ReportAppServer.DataDefModel.Table table, XmlWriter writer)
		{
			writer.WriteStartElement("Table");

			WriteAttributeString(writer, "Alias", table.Alias);
			WriteAttributeString(writer, "ClassName", table.ClassName);
			WriteAttributeString(writer, "Name", table.Name);

			writer.WriteStartElement("ConnectionInfo");
			// a property that is itself a property bag is written as a Property element after the attributes
			var nestedProperties = new System.Collections.Generic.List<PropertyBagEntry>();
			foreach (string propertyId in table.ConnectionInfo.Attributes.PropertyIDs)
			{
				object value = table.ConnectionInfo.Attributes[propertyId];
				PropertyBagEntry nestedProperty = TryReadNestedProperty(table.Alias, propertyId, value);
				if (nestedProperty != null)
				{
					nestedProperties.Add(nestedProperty);
					continue;
				}

				// make attribute name safe for XML
				string attributeName = propertyId.Replace(" ", "_");

				WriteAttributeString(writer, attributeName, value.ToString());
			}

			WriteAttributeString(writer, "UserName", table.ConnectionInfo.UserName);
			WriteAttributeString(writer, "Password", table.ConnectionInfo.Password);
			WritePropertyBagEntries(writer, "Property", nestedProperties.ToArray());
			writer.WriteEndElement();

            if (table is CRDataDefModel.CommandTable commandTable)
			{
				var cmdTable = commandTable;
				writer.WriteStartElement("Command");
				WriteString(writer, cmdTable.CommandText);
				writer.WriteEndElement();
			}

			writer.WriteStartElement("Fields");

			foreach (CrystalDecisions.ReportAppServer.DataDefModel.Field fd in table.DataFields)
			{
				GetFieldDefinition(fd, writer);
			}

			writer.WriteEndElement();

			writer.WriteEndElement();
		}

		private static void GetFieldDefinition(FieldDefinition fd, XmlWriter writer)
		{
			writer.WriteStartElement("Field");

			WriteAttributeString(writer, "FormulaName", fd.FormulaName);
			WriteAttributeString(writer, "Kind", fd.Kind.ToString());
			WriteAttributeString(writer, "Name", fd.Name);
			WriteAttributeString(writer, "NumberOfBytes", fd.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "ValueType", fd.ValueType.ToString());

			writer.WriteEndElement();
		}

		private static void GetFieldDefinition(CrystalDecisions.ReportAppServer.DataDefModel.Field fd, XmlWriter writer)
		{
			writer.WriteStartElement("Field");

			WriteAttributeString(writer, "Description", fd.Description);
			WriteAttributeString(writer, "FormulaForm", fd.FormulaForm);
			WriteAttributeString(writer, "HeadingText", fd.HeadingText);
			WriteAttributeString(writer, "IsRecurring", fd.IsRecurring.ToString());
			WriteAttributeString(writer, "Kind", fd.Kind.ToString());
			WriteAttributeString(writer, "Length", fd.Length.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "LongName", fd.LongName);
			WriteAttributeString(writer, "Name", fd.Name);
			WriteAttributeString(writer, "ShortName", fd.ShortName);
			WriteAttributeString(writer, "Type", fd.Type.ToString());
			WriteAttributeString(writer, "UseCount", fd.UseCount.ToString(CultureInfo.InvariantCulture));

			writer.WriteEndElement();
		}

		[HandleProcessCorruptedStateExceptions]
		private void GetDataDefinition(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("DataDefinition");

			WriteElementString(writer, "GroupSelectionFormula", report.DataDefinition.GroupSelectionFormula);
			WriteElementString(writer, "RecordSelectionFormula", report.DataDefinition.RecordSelectionFormula);

			writer.WriteStartElement("Groups");
			// engine and RAS groups are both in group-level order; pair them by index (see GetRASDDMGroups)
			CRDataDefModel.Groups ddmGroups = null;
			try
			{
				ddmGroups = GetRASDDMGroups(report);
				int groupCount = report.DataDefinition.Groups.Count;
				int rasGroupCount = ddmGroups?.Count ?? 0;
				if (rasGroupCount != groupCount)
				{
					ddmGroups = null; // cannot pair them reliably, so write no options rather than wrong ones
					Console.Error.WriteLine($"Error reading group options of {DescribeReport(report)}, it has {groupCount} groups and RAS has {rasGroupCount}");
				}
			}
			catch (Exception e)
			{
				ddmGroups = null; // not checked against the engine's groups, so do not pair them
				Console.Error.WriteLine($"Error reading group options of {DescribeReport(report)}, {e.Message}");
			}
			int groupIndex = 0;
			foreach (Group group in report.DataDefinition.Groups)
			{
				writer.WriteStartElement("Group");
				string conditionFieldName = group.ConditionField.FormulaName;
				WriteAttributeString(writer, "ConditionField", conditionFieldName);

				if (ddmGroups != null)
				{
					int index = groupIndex;
					string groupDescription = $"group {conditionFieldName} in {DescribeReport(report)}";
					WriteOptional(writer, $"options of {groupDescription}", w =>
					{
						CRDataDefModel.Group ddmGroup = ddmGroups[index];
						CheckRASField(ddmGroup.ConditionField, conditionFieldName, $"the RAS group at position {index + 1}");
						GetGroupOptions(ddmGroup.Options, groupDescription, w);
					});
				}
				groupIndex++;

				writer.WriteEndElement();

			}
			writer.WriteEndElement();

			writer.WriteStartElement("SortFields");
			int sortIndex = 0;
			foreach (SortField sortField in report.DataDefinition.SortFields)
			{
				writer.WriteStartElement("SortField");

				WriteAttributeString(writer, "Field", sortField.Field.FormulaName);
				try
				{
					string sortDirection = sortField.SortDirection.ToString();
					WriteAttributeString(writer, "SortDirection", sortDirection);
				}
				catch (NotSupportedException)
				{ }
				WriteAttributeString(writer, "SortType", sortField.SortType.ToString());

				if (sortField is TopBottomNSortField)
				{
					int index = sortIndex;
					string sortFieldName = sortField.Field.FormulaName;
					WriteOptional(writer, $"TopN sort of {sortFieldName} in {DescribeReport(report)}", w => GetTopNSort(report, index, sortFieldName, w));
				}
				sortIndex++;

				writer.WriteEndElement();
			}
			writer.WriteEndElement();

			writer.WriteStartElement("FormulaFieldDefinitions");
			foreach (var field in report.DataDefinition.FormulaFields.OfType<FieldDefinition>().OrderBy(field => field.FormulaName))
            {
                GetFieldObject(field, report, writer);
            }

            writer.WriteEndElement();

			writer.WriteStartElement("GroupNameFieldDefinitions");
			foreach (var field in report.DataDefinition.GroupNameFields)
            {
                GetFieldObject(field, report, writer);
            }

            writer.WriteEndElement();

			writer.WriteStartElement("ParameterFieldDefinitions");
			// As in GetSubreports: the outer try covers enumerating the parameters, and each parameter is written through
			// WriteBuffered, so one that fails part-way through is replaced by a marker element and the rest are still written.
			try
			{
				foreach (var field in report.DataDefinition.ParameterFields)
				{
					try
					{
						WriteBuffered(writer, w => GetFieldObject(field, report, w));
					}
					catch (Exception e)
					{
						string name = TryGetName(() => (field as ParameterFieldDefinition)?.Name);
						Console.Error.WriteLine($"Error processing parameter '{name}', {e}");
						writer.WriteStartElement("ParameterFieldDefinition");
						WriteAttributeString(writer, "Name", name);
						WriteAttributeString(writer, "Error", e.GetType().Name); // not the message, which can name a temporary file
						writer.WriteEndElement();
					}
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error processing ParameterFieldDefinitions, {e}");
			}
			writer.WriteEndElement();

			writer.WriteStartElement("RunningTotalFieldDefinitions");
			foreach (var field in report.DataDefinition.RunningTotalFields)
            {
                GetFieldObject(field, report, writer);
            }

            writer.WriteEndElement();

			writer.WriteStartElement("SQLExpressionFields");
			foreach (var field in report.DataDefinition.SQLExpressionFields)
            {
                GetFieldObject(field, report, writer);
            }

            writer.WriteEndElement();

			writer.WriteStartElement("SummaryFields");
			foreach (var field in report.DataDefinition.SummaryFields)
            {
                GetFieldObject(field, report, writer);
            }

            writer.WriteEndElement();

			writer.WriteEndElement();
		}

		[HandleProcessCorruptedStateExceptions]
		private void GetFieldObject(Object fo, ReportDocument report, XmlWriter writer)
		{
			if (fo is DatabaseFieldDefinition df)
			{
                writer.WriteStartElement("DatabaseFieldDefinition");

				WriteAttributeString(writer, "FormulaName", df.FormulaName);
				WriteAttributeString(writer, "Kind", df.Kind.ToString());
				WriteAttributeString(writer, "Name", df.Name);
				WriteAttributeString(writer, "NumberOfBytes", df.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "TableName", df.TableName);
				WriteAttributeString(writer, "ValueType", df.ValueType.ToString());

			}
			else if (fo is FormulaFieldDefinition ff)
			{
				var ddm_ff = GetRASDDMFormulaFieldObject(ff.Name, report);
			

				writer.WriteStartElement("FormulaFieldDefinition");

				WriteAttributeString(writer, "FormulaName", ff.FormulaName);
				WriteAttributeString(writer, "Kind", ff.Kind.ToString());
				WriteAttributeString(writer, "Name", ff.Name);
				WriteAttributeString(writer, "NumberOfBytes", ff.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "ValueType", ff.ValueType.ToString());
				if (ddm_ff != null)
				{
					WriteAttributeString(writer, "Syntax", ddm_ff.Syntax.ToString());
				}
				WriteString(writer, ff.Text);

			}
			else if (fo is GroupNameFieldDefinition gnf)
			{
                writer.WriteStartElement("GroupNameFieldDefinition");
				try
				{
					WriteAttributeString(writer, "FormulaName", gnf.FormulaName);
					WriteOptionalAttribute(writer, "Group", "Group of a group name field", () => GetGroupReference(gnf.Group));
					WriteAttributeString(writer, "GroupNameFieldName", gnf.GroupNameFieldName);
					WriteAttributeString(writer, "Kind", gnf.Kind.ToString());
					WriteAttributeString(writer, "Name", gnf.Name);
					WriteAttributeString(writer, "NumberOfBytes", gnf.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "ValueType", gnf.ValueType.ToString());
				}
				catch (Exception e)
				{
					Console.Error.WriteLine($"Error loading formula for group '{TryGetName(() => gnf.GroupNameFieldName)}', {e}");
				}
			}
			else if (fo is ParameterFieldDefinition pf)
			{
                // if it is a linked parameter, it is passed into a subreport. Just record the actual linkage in the main report.
				// The parameter will be reported in full when the subreport is exported.  
				var parameterIsLinked = (!report.IsSubreport && pf.IsLinked());

				writer.WriteStartElement("ParameterFieldDefinition");

				if (parameterIsLinked)
				{
					WriteAttributeString(writer, "Name", pf.Name);
					WriteAttributeString(writer, "IsLinkedToSubreport", pf.IsLinked().ToString());
					WriteAttributeString(writer, "ReportName", pf.ReportName);
				}
				else
				{
					var ddm_pf = GetRASDDMParameterFieldObject(pf, report);

					WriteAttributeString(writer, "AllowCustomCurrentValues", (ddm_pf != null && ddm_pf.AllowCustomCurrentValues).ToString());
					WriteParameterSettingAttribute(writer, pf.Name, "DefaultValueDisplayType", () => pf.DefaultValueDisplayType.ToString());
					WriteParameterSettingAttribute(writer, pf.Name, "DefaultValueSortMethod", () => pf.DefaultValueSortMethod.ToString());
					WriteParameterSettingAttribute(writer, pf.Name, "DefaultValueSortOrder", () => pf.DefaultValueSortOrder.ToString());
					WriteParameterSettingAttribute(writer, pf.Name, "DiscreteOrRangeKind", () => pf.DiscreteOrRangeKind.ToString());
					WriteAttributeString(writer, "EditMask", pf.EditMask);
					WriteAttributeString(writer, "EnableAllowEditingDefaultValue", pf.EnableAllowEditingDefaultValue.ToString());
					WriteAttributeString(writer, "EnableAllowMultipleValue", pf.EnableAllowMultipleValue.ToString());
					WriteAttributeString(writer, "EnableNullValue", pf.EnableNullValue.ToString());
					WriteAttributeString(writer, "FormulaName", pf.FormulaName);
					WriteAttributeString(writer, "HasCurrentValue", pf.HasCurrentValue.ToString());
					WriteAttributeString(writer, "IsOptionalPrompt", pf.IsOptionalPrompt.ToString());
					WriteAttributeString(writer, "Kind", pf.Kind.ToString());
					WriteParameterSettingAttribute(writer, pf.Name, "MaximumValue", () => FormatParameterValue(pf.MaximumValue));
					WriteParameterSettingAttribute(writer, pf.Name, "MinimumValue", () => FormatParameterValue(pf.MinimumValue));
					WriteAttributeString(writer, "Name", pf.Name);
					WriteAttributeString(writer, "NumberOfBytes", pf.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "ParameterFieldName", pf.ParameterFieldName);
					WriteAttributeString(writer, "ParameterFieldUsage", pf.ParameterFieldUsage2.ToString());
					WriteAttributeString(writer, "ParameterType", pf.ParameterType.ToString());
					WriteAttributeString(writer, "ParameterValueKind", pf.ParameterValueKind.ToString());
					WriteAttributeString(writer, "PromptText", pf.PromptText);
					WriteAttributeString(writer, "ReportName", pf.ReportName);
					WriteAttributeString(writer, "ValueType", pf.ValueType.ToString());

					writer.WriteStartElement("ParameterDefaultValues");
					if (pf.DefaultValues.Count > 0)
					{
						foreach (ParameterValue pv in pf.DefaultValues)
						{
							writer.WriteStartElement("ParameterDefaultValue");
							WriteAttributeString(writer, "Description", pv.Description);
							WriteParameterValueAttributes(writer, pv);
							writer.WriteEndElement();
						}
					}
					writer.WriteEndElement();

					writer.WriteStartElement("ParameterInitialValues");
					if (ddm_pf != null)
					{
						if (ddm_pf.InitialValues.Count > 0)
						{
							foreach (object pv in ddm_pf.InitialValues)
							{
								writer.WriteStartElement("ParameterInitialValue");
								WriteParameterValueAttributes(writer, pv);
								writer.WriteEndElement();
							}
						}
					}
					writer.WriteEndElement();

					writer.WriteStartElement("ParameterCurrentValues");
					if (pf.CurrentValues.Count > 0)
					{
						foreach (ParameterValue pv in pf.CurrentValues)
						{
							writer.WriteStartElement("ParameterCurrentValue");
							WriteAttributeString(writer, "Description", pv.Description);
							WriteParameterValueAttributes(writer, pv);
							writer.WriteEndElement();
						}
					}
					writer.WriteEndElement();

					if (ddm_pf != null)
					{
						GetParameterPrompting(ddm_pf, pf.Name, writer);
					}
				}

			}
			else if (fo is RunningTotalFieldDefinition rtf)
			{
                writer.WriteStartElement("RunningTotalFieldDefinition");
				WriteRunningTotalConditionAttribute(writer, "EvaluationCondition", rtf.EvaluationConditionType, () => rtf.EvaluationCondition);
				WriteAttributeString(writer, "EvaluationConditionType", rtf.EvaluationConditionType.ToString());
				WriteAttributeString(writer, "FormulaName", rtf.FormulaName);
				if (rtf.Group != null)
                {
                    WriteAttributeString(writer, "Group", GetGroupReference(rtf.Group));
                }

                WriteAttributeString(writer, "Kind", rtf.Kind.ToString());
				WriteAttributeString(writer, "Name", rtf.Name);
				WriteAttributeString(writer, "NumberOfBytes", rtf.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Operation", rtf.Operation.ToString());
				WriteAttributeString(writer, "OperationParameter", rtf.OperationParameter.ToString(CultureInfo.InvariantCulture));
				WriteRunningTotalConditionAttribute(writer, "ResetCondition", rtf.ResetConditionType, () => rtf.ResetCondition);
				WriteAttributeString(writer, "ResetConditionType", rtf.ResetConditionType.ToString());

				if (rtf.SecondarySummarizedField != null)
                {
                    WriteAttributeString(writer, "SecondarySummarizedField", rtf.SecondarySummarizedField.FormulaName);
                }

                WriteAttributeString(writer, "SummarizedField", rtf.SummarizedField.FormulaName);
				WriteAttributeString(writer, "ValueType", rtf.ValueType.ToString());

			}
			else if (fo is SpecialVarFieldDefinition svf)
			{
				writer.WriteStartElement("SpecialVarFieldDefinition");
                WriteAttributeString(writer, "FormulaName", svf.FormulaName);
				WriteAttributeString(writer, "Kind", svf.Kind.ToString());
				WriteAttributeString(writer, "Name", svf.Name);
				WriteAttributeString(writer, "NumberOfBytes", svf.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "SpecialVarType", svf.SpecialVarType.ToString());
				WriteAttributeString(writer, "ValueType", svf.ValueType.ToString());

			}
			else if (fo is SQLExpressionFieldDefinition sef)
			{
				writer.WriteStartElement("SQLExpressionFieldDefinition");

                WriteAttributeString(writer, "FormulaName", sef.FormulaName);
				WriteAttributeString(writer, "Kind", sef.Kind.ToString());
				WriteAttributeString(writer, "Name", sef.Name);
				WriteAttributeString(writer, "NumberOfBytes", sef.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Text", sef.Text);
				WriteAttributeString(writer, "ValueType", sef.ValueType.ToString());

			}
			else if (fo is SummaryFieldDefinition sf)
			{
				writer.WriteStartElement("SummaryFieldDefinition");

                WriteAttributeString(writer, "FormulaName", sf.FormulaName);

				if (sf.Group != null)
                {
                    WriteOptionalAttribute(writer, "Group", "Group of a summary field", () => GetGroupReference(sf.Group));
                }

				WriteOptionalAttribute(writer, "IsPercentageSummary", "IsPercentageSummary of a summary field", () => sf.IsPercentageSummary.ToString());
                WriteAttributeString(writer, "Kind", sf.Kind.ToString());
				WriteAttributeString(writer, "Name", sf.Name);
				WriteAttributeString(writer, "NumberOfBytes", sf.NumberOfBytes.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Operation", sf.Operation.ToString());
				WriteAttributeString(writer, "OperationParameter", sf.OperationParameter.ToString(CultureInfo.InvariantCulture));
				if (sf.SecondarySummarizedField != null)
                {
                    WriteOptionalAttribute(writer, "SecondarySummarizedField", "SecondarySummarizedField of a summary field", () => GetFieldReference(sf.SecondarySummarizedField));
                }

                WriteOptionalAttribute(writer, "SummarizedField", "SummarizedField of a summary field", () => GetFieldReference(sf.SummarizedField));
				WriteAttributeString(writer, "ValueType", sf.ValueType.ToString());

			}
			writer.WriteEndElement();
		}

		// Engine fields and groups don't override ToString(), so reference them the way the rest of the dump does:
		// a field by its formula name (e.g. {Orders.Amount}), a group by its condition field's formula name
		// (as in DataDefinition/Groups/Group/@ConditionField).
		private static string GetFieldReference(FieldDefinition field)
		{
			return field?.FormulaName;
		}

		private static string GetGroupReference(Group group)
		{
			return group?.ConditionField?.FormulaName;
		}

		// Names a report in an error message; the main report's Name is empty.
		private static string DescribeReport(ReportDocument report)
		{
			return report.IsSubreport ? $"subreport '{report.Name}'" : "the main report";
		}

		// RunningTotalFieldDefinition.EvaluationCondition/ResetCondition is, per its *ConditionType: NoCondition -> null,
		// OnChangeOfField -> FieldDefinition, OnChangeOfGroup -> Group, OnFormula -> the formula text (string).
		// A failed read is reported and omits the attribute. The engine's conversion throws NotSupportedException for a RAS object it does not
		// map and NotImplementedException for a field kind it does not map (e.g. a group name field), and COM interop raises a
		// failing RAS getter as COMException or, for standard HRESULTs, as NotImplementedException, ArgumentException,
		// InvalidCastException and others, so any exception counts as a failed read.
		[HandleProcessCorruptedStateExceptions]
		private static void WriteRunningTotalConditionAttribute(XmlWriter writer, string name, RunningTotalCondition conditionType, Func<object> getCondition)
		{
			if (conditionType == RunningTotalCondition.NoCondition)
			{
				return;
			}

			string value;
			try
			{
				object condition = getCondition();
				switch (condition)
				{
					case string formulaText:
						value = formulaText;
						break;
					case FormulaFieldDefinition formula when conditionType == RunningTotalCondition.OnFormula:
						value = formula.Text;
						break;
					case FieldDefinition field:
						value = GetFieldReference(field);
						break;
					case Group group:
						value = GetGroupReference(group);
						break;
					default:
						value = null;
						break;
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error reading {name} of a running total field, {e.Message}");
				return;
			}

			if (!String.IsNullOrEmpty(value))
			{
				WriteAttributeString(writer, name, value);
			}
		}

		// The main report's parameters include those of its subreports, whose ReportName is the subreport's name (it is empty
		// for the main report's own), so a parameter is looked up in the report that it belongs to.
		[HandleProcessCorruptedStateExceptions]
		private CRDataDefModel.ParameterField GetRASDDMParameterFieldObject(ParameterFieldDefinition pf, ReportDocument report)
		{
			CRDataDefModel.ParameterField rdm;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				rdm = subrptClientDoc.DataDefController.DataDefinition.ParameterFields.FindField(pf.Name,
					CRDataDefModel.CrFieldDisplayNameTypeEnum.crFieldDisplayNameName) as CRDataDefModel.ParameterField;
			}
			else if (!String.IsNullOrEmpty(pf.ReportName))
			{
				// Earlier versions of this tool looked such a parameter up in the main report. If the lookup in the subreport
				// fails, the parameter is written without the RAS settings, as when it is not found, and the failure reported.
				try
				{
					var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(pf.ReportName);
					rdm = subrptClientDoc.DataDefController.DataDefinition.ParameterFields.FindField(pf.Name,
						CRDataDefModel.CrFieldDisplayNameTypeEnum.crFieldDisplayNameName) as CRDataDefModel.ParameterField;
				}
				catch (Exception e)
				{
					Console.Error.WriteLine($"Error reading parameter '{pf.Name}' of subreport '{pf.ReportName}', {e.Message}");
					rdm = null;
				}
			}
			else
			{
				rdm = _rcd.DataDefController.DataDefinition.ParameterFields.FindField(pf.Name,
					CRDataDefModel.CrFieldDisplayNameTypeEnum.crFieldDisplayNameName) as CRDataDefModel.ParameterField;
			}
			return rdm;
		}

		// Prompting settings read from the RAS parameter object, including what makes a parameter dynamic or cascading. The engine's
		// ParameterFieldDefinition exposes none of them except the attribute bag (as a Hashtable converted from the same bag), and
		// RAS IsShownOnPanel / IsEditableOnPanel are not written because ParameterFieldUsage already shows them.
		// The values a dynamic prompt offers come from the data source at prompt time and are not dumped. The definition of its
		// list of values (data source, value and description fields) is stored in the report (the PromptManager stream) and is
		// NOT dumped either: the SDK declares types for it (ReportSource.GetParamPromptingInfo, Prompting.ILOVDataSource,
		// IPromptGroup), but whether they work on a report loaded from a file without a database logon is untested.
		// Dumped: the field the values are browsed from, the parent parameters of a cascading prompt, the function supplying
		// initial values, and the parameter's attribute bag.
		// Each setting is read on its own and in full before it is written (see TryReadParameterSetting), so a getter that is not
		// supported for this kind of parameter only leaves out its own attribute or element, never an empty or half-written one.
		private void GetParameterPrompting(CRDataDefModel.ParameterField ddm_pf, string parameterName, XmlWriter writer)
		{
			writer.WriteStartElement("ParameterPrompting");

			WriteParameterSettingAttribute(writer, parameterName, "AllowHierarchyValues", () => ddm_pf.AllowHierarchyValues.ToString());
			WriteParameterSettingAttribute(writer, parameterName, "BrowseField", () =>
			{
				string formulaForm = ddm_pf.BrowseField?.FormulaForm;
				return string.IsNullOrEmpty(formulaForm) ? null : formulaForm;
			});
			WriteParameterSettingAttribute(writer, parameterName, "IsDataFoundationParameter", () => ddm_pf.IsDataFoundationParameter.ToString());
			WriteParameterSettingAttribute(writer, parameterName, "KeepLastValueSelected", () => ddm_pf.KeepLastValueSelected.ToString());

			// cascading prompt: the parameters answered before this one offers its list of values, in their stored order
			string[] prerequisiteNames = TryReadParameterSetting(parameterName, "DirectPrerequisiteParameterNames",
				() => ddm_pf.DirectPrerequisiteParameterNames?.Cast<string>().ToArray() ?? new string[0]);
			if (prerequisiteNames != null)
			{
				writer.WriteStartElement("DirectPrerequisiteParameters");
				foreach (string prerequisiteName in prerequisiteNames)
				{
					writer.WriteStartElement("DirectPrerequisiteParameter");
					WriteAttributeString(writer, "Name", prerequisiteName);
					writer.WriteEndElement();
				}
				writer.WriteEndElement();
			}

			// name, syntax and text; RAS may return an empty function rather than none, which is left out like a missing one
			Tuple<string, string, string> initialValuesFunction = TryReadParameterSetting(parameterName, "InitialValuesFunction", () =>
			{
				CRDataDefModel.CustomFunction function = ddm_pf.InitialValuesFunction;
				return function == null || (string.IsNullOrEmpty(function.Name) && string.IsNullOrEmpty(function.Text))
					? null
					: Tuple.Create(function.Name, function.Syntax.ToString(), function.Text);
			});
			if (initialValuesFunction != null)
			{
				writer.WriteStartElement("InitialValuesFunction");
				WriteAttributeString(writer, "Name", initialValuesFunction.Item1);
				WriteAttributeString(writer, "Syntax", initialValuesFunction.Item2);
				writer.WriteStartElement("Text"); // an element so line breaks are literal
				WriteString(writer, initialValuesFunction.Item3);
				writer.WriteEndElement();
				writer.WriteEndElement();
			}

			PropertyBagEntry[] attributes = TryReadParameterSetting(parameterName, "Attributes", () =>
			{
				CRDataDefModel.PropertyBag bag = ddm_pf.Attributes;
				return bag == null ? new PropertyBagEntry[0] : ReadPropertyBag(bag);
			});
			if (attributes != null)
			{
				writer.WriteStartElement("ParameterAttributes");
				WritePropertyBagEntries(writer, "ParameterAttribute", attributes);
				writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

		// One entry of a RAS property bag, copied out of RAS so that writing it reads nothing more from RAS.
		private sealed class PropertyBagEntry
		{
			public string Name;
			public string Value; // formatted by FormatParameterValue; null when missing, left out (see ReadPropertyBag) or for a nested bag
			public PropertyBagEntry[] NestedEntries; // a nested bag, else null
		}

		private static readonly System.Text.RegularExpressions.Regex PasswordKeyRegex =
			new System.Text.RegularExpressions.Regex("password|passwd|pwd", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		private static readonly System.Text.RegularExpressions.Regex PasswordValueRegex =
			new System.Text.RegularExpressions.Regex("\\b(password|pwd)\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

		// Sorted by name so the output does not depend on the bag's internal order. A nested bag becomes nested entries.
		// The value is left out when the name looks like a password or the value contains one (as in "...;PWD=x;").
		private static PropertyBagEntry[] ReadPropertyBag(CRDataDefModel.PropertyBag bag)
		{
			return bag.PropertyIDs.Cast<string>()
				.OrderBy(id => id, StringComparer.Ordinal)
				.Select(id =>
				{
					object value = bag[id];
					var nestedBag = value as CRDataDefModel.PropertyBag;
					string text = nestedBag == null ? FormatParameterValue(value) : null;
					if (text != null && (PasswordKeyRegex.IsMatch(id) || PasswordValueRegex.IsMatch(text)))
					{
						text = null;
					}
					return new PropertyBagEntry
					{
						Name = id,
						Value = text,
						NestedEntries = nestedBag == null ? null : ReadPropertyBag(nestedBag),
					};
				})
				.ToArray();
		}

		// Reads a connection property whose value is itself a property bag (QE_LogonProperties, the logon settings of the
		// connection), which earlier versions of this tool wrote as "System.__ComObject". Returns null for any other value,
		// and when the bag cannot be read (reported), so that the property is written as an attribute as before.
		[HandleProcessCorruptedStateExceptions]
		private static PropertyBagEntry TryReadNestedProperty(string tableAlias, string propertyId, object value)
		{
			try
			{
				var bag = value as CRDataDefModel.PropertyBag;
				return bag == null ? null : new PropertyBagEntry { Name = propertyId, NestedEntries = ReadPropertyBag(bag) };
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error reading {propertyId} of table '{tableAlias}', {e.Message}");
				return null;
			}
		}

		private static void WritePropertyBagEntries(XmlWriter writer, string elementName, PropertyBagEntry[] entries)
		{
			foreach (PropertyBagEntry entry in entries)
			{
				writer.WriteStartElement(elementName);
				WriteAttributeString(writer, "Name", entry.Name);
				if (entry.NestedEntries != null)
				{
					WritePropertyBagEntries(writer, elementName, entry.NestedEntries);
				}
				else if (entry.Value != null)
				{
					WriteAttributeString(writer, "Value", entry.Value);
				}
				writer.WriteEndElement();
			}
		}

		// Reads one setting of a parameter in full, or returns null when the read fails. A RAS getter can fail for a kind of
		// parameter it does not support, and COM interop raises that as COMException or, for standard HRESULTs, as
		// NotImplementedException, ArgumentException, InvalidCastException and others, so any exception counts as a failed read.
		// Nothing is written while reading, so a failure cannot leave an element half-written or drop the remaining parameters.
		[HandleProcessCorruptedStateExceptions]
		private T TryReadParameterSetting<T>(string parameterName, string setting, Func<T> read) where T : class
		{
			try
			{
				return read();
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error reading {setting} of parameter '{parameterName}', {e.Message}");
				return null;
			}
		}

		// Writes the attribute unless its value is missing (null) or cannot be read.
		private void WriteParameterSettingAttribute(XmlWriter writer, string parameterName, string name, Func<string> read)
		{
			string value = TryReadParameterSetting(parameterName, name, read);
			if (value != null)
			{
				WriteAttributeString(writer, name, value);
			}
		}

		// Writes one parameter value, from the engine (Shared.ParameterValue) or from RAS (ISCRValue): Value for a discrete
		// value; StartValue/EndValue and the bound types for a range value. Discrete is tested before range, in the order the
		// engine tests RAS values when it converts them.
		private static void WriteParameterValueAttributes(XmlWriter writer, object parameterValue)
		{
			switch (parameterValue)
			{
				case ParameterDiscreteValue discrete:
					WriteParameterValueAttribute(writer, "Value", discrete.Value);
					break;
				case ParameterRangeValue range:
					WriteParameterRangeAttributes(writer, range.StartValue, range.LowerBoundType, range.EndValue, range.UpperBoundType);
					break;
				case CRDataDefModel.ISCRParameterFieldDiscreteValue rasDiscrete:
					WriteParameterValueAttribute(writer, "Value", rasDiscrete.Value);
					break;
				case CRDataDefModel.ISCRParameterFieldRangeValue rasRange:
					// CrRangeValueBoundTypeEnum and RangeBoundType share their values (no bound / exclusive / inclusive = 0 / 1 / 2),
					// so RAS ranges are written with the same bound names as engine ranges
					WriteParameterRangeAttributes(writer, rasRange.BeginValue, (RangeBoundType)rasRange.LowerBoundType, rasRange.EndValue, (RangeBoundType)rasRange.UpperBoundType);
					break;
				case CRDataDefModel.ISCRConstantValue rasConstant:
					WriteParameterValueAttribute(writer, "Value", rasConstant.Value);
					break;
			}
		}

		// An unbounded end has no value (the engine nulls it), so StartValue/EndValue are written only for bounded ends.
		private static void WriteParameterRangeAttributes(XmlWriter writer, object startValue, RangeBoundType lowerBoundType, object endValue, RangeBoundType upperBoundType)
		{
			if (lowerBoundType != RangeBoundType.NoBound)
			{
				WriteParameterValueAttribute(writer, "StartValue", startValue);
			}
			if (upperBoundType != RangeBoundType.NoBound)
			{
				WriteParameterValueAttribute(writer, "EndValue", endValue);
			}
			WriteAttributeString(writer, "LowerBoundType", lowerBoundType.ToString());
			WriteAttributeString(writer, "UpperBoundType", upperBoundType.ToString());
		}

		// Omits the attribute when the value is missing (see FormatParameterValue).
		private static void WriteParameterValueAttribute(XmlWriter writer, string name, object value)
		{
			string text = FormatParameterValue(value);
			if (text != null)
			{
				WriteAttributeString(writer, name, text);
			}
		}

		// Formats a parameter value independently of the current culture, so the XML is the same on every machine.
		// Strings and booleans come out as ToString() always gave them, numbers use the invariant culture, and dates/times use
		// ISO 8601 "yyyy-MM-ddTHH:mm:ss" with fractional seconds only when present: culture-independent, sorts chronologically
		// as text, round-trips through DateTime.Parse with the invariant culture, and involves no time-zone conversion.
		// Returns null for a missing value (null or DBNull) or one without a text form (e.g. a COM object) rather than a type name.
		private static string FormatParameterValue(object value)
		{
			switch (value)
			{
				case null:
				case DBNull _:
					return null;
				case string text:
					return text;
				case DateTime dateTime:
					return dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
				case IConvertible convertible:
					return convertible.ToString(CultureInfo.InvariantCulture);
				case IFormattable formattable:
					return formattable.ToString(null, CultureInfo.InvariantCulture);
				default:
					return null;
			}
		}

		private CRDataDefModel.FormulaField GetRASDDMFormulaFieldObject(string fieldName, ReportDocument report)
		{
			CRDataDefModel.FormulaField rdm;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				rdm = subrptClientDoc.DataDefController.DataDefinition.FormulaFields.FindField(fieldName,
					CRDataDefModel.CrFieldDisplayNameTypeEnum.crFieldDisplayNameName) as CRDataDefModel.FormulaField;
			}
			else
			{
				rdm = _rcd.DataDefController.DataDefinition.FormulaFields.FindField(fieldName,
					CRDataDefModel.CrFieldDisplayNameTypeEnum.crFieldDisplayNameName) as CRDataDefModel.FormulaField;
			}
			return rdm;
		}

		private void GetAreaFormat(Area area, ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("AreaFormat");

			WriteAttributeString(writer, "EnableHideForDrillDown", area.AreaFormat.EnableHideForDrillDown.ToString());
			WriteAttributeString(writer, "EnableKeepTogether", area.AreaFormat.EnableKeepTogether.ToString());
			WriteAttributeString(writer, "EnableNewPageAfter", area.AreaFormat.EnableNewPageAfter.ToString());
			WriteAttributeString(writer, "EnableNewPageBefore", area.AreaFormat.EnableNewPageBefore.ToString());
			WriteAttributeString(writer, "EnablePrintAtBottomOfPage", area.AreaFormat.EnablePrintAtBottomOfPage.ToString());
			WriteAttributeString(writer, "EnableResetPageNumberAfter", area.AreaFormat.EnableResetPageNumberAfter.ToString());
			WriteAttributeString(writer, "EnableSuppress", area.AreaFormat.EnableSuppress.ToString());

			if (area.Kind == AreaSectionKind.GroupHeader)
			{
				GroupAreaFormat gaf = (GroupAreaFormat)area.AreaFormat;
				writer.WriteStartElement("GroupAreaFormat");
				WriteAttributeString(writer, "EnableKeepGroupTogether", gaf.EnableKeepGroupTogether.ToString());
				WriteAttributeString(writer, "EnableRepeatGroupHeader", gaf.EnableRepeatGroupHeader.ToString());
				WriteAttributeString(writer, "VisibleGroupNumberPerPage", gaf.VisibleGroupNumberPerPage.ToString());
				writer.WriteEndElement();
			}

			WriteOptional(writer, $"condition formulas of area {area.Name}", w =>
			{
				CRReportDefModel.ISCRArea rdmArea = GetRASRDMAreaObjectFromCRENGAreaObject(area.Name, report);
				if (rdmArea != null)
				{
					GetAreaFormatConditionFormulas(rdmArea, w);
				}
			});

			writer.WriteEndElement();

		}

		private void GetBorderFormat(ReportObject ro, ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Border");

			var border = ro.Border;
			WriteAttributeString(writer, "BottomLineStyle", border.BottomLineStyle.ToString());
			WriteAttributeString(writer, "HasDropShadow", border.HasDropShadow.ToString());
			WriteAttributeString(writer, "LeftLineStyle", border.LeftLineStyle.ToString());
			WriteAttributeString(writer, "RightLineStyle", border.RightLineStyle.ToString());
			WriteAttributeString(writer, "TopLineStyle", border.TopLineStyle.ToString());

			CRReportDefModel.ISCRReportObject rdmRo = GetRASRDMReportObject(ro.Name, report);
			if (rdmRo != null)
            {
                GetBorderConditionFormulas(rdmRo, writer);
            }

            if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
            {
                GetColorFormat(border.BackgroundColor, writer, "BackgroundColor");
            }

            if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
            {
                GetColorFormat(border.BorderColor, writer, "BorderColor");
            }

            writer.WriteEndElement();
		}

		private static void GetColorFormat(Color color, XmlWriter writer, String elementName = "Color")
		{
			writer.WriteStartElement(elementName);

			WriteAttributeString(writer, "Name", color.Name);
			WriteAttributeString(writer, "A", color.A.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "R", color.R.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "G", color.G.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "B", color.B.ToString(CultureInfo.InvariantCulture));

			writer.WriteEndElement();
		}

		private void GetFontFormat(Font font, XmlWriter writer)
		{
			writer.WriteStartElement("Font");

			WriteAttributeString(writer, "Bold", font.Bold.ToString());
			WriteAttributeString(writer, "FontFamily", font.FontFamily.Name);
			WriteAttributeString(writer, "GdiCharSet", font.GdiCharSet.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "GdiVerticalFont", font.GdiVerticalFont.ToString());
			WriteAttributeString(writer, "Height", font.Height.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "IsSystemFont", font.IsSystemFont.ToString());
			WriteAttributeString(writer, "Italic", font.Italic.ToString());
			WriteAttributeString(writer, "Name", font.Name);
			WriteAttributeString(writer, "OriginalFontName", font.OriginalFontName);
			WriteAttributeString(writer, "Size", font.Size.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "SizeinPoints", font.SizeInPoints.ToString(CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "Strikeout", font.Strikeout.ToString());
			WriteAttributeString(writer, "Style", font.Style.ToString());
			WriteAttributeString(writer, "SystemFontName", font.SystemFontName);
			WriteAttributeString(writer, "Underline", font.Underline.ToString());
			WriteAttributeString(writer, "Unit", font.Unit.ToString());

			writer.WriteEndElement();
		}

		private void GetObjectFormat(ReportObject ro, XmlWriter writer)
		{
			writer.WriteStartElement("ObjectFormat");


			WriteAttributeString(writer, "CssClass", ro.ObjectFormat.CssClass);
			WriteAttributeString(writer, "EnableCanGrow", ro.ObjectFormat.EnableCanGrow.ToString());
			WriteAttributeString(writer, "EnableCloseAtPageBreak", ro.ObjectFormat.EnableCloseAtPageBreak.ToString());
			WriteAttributeString(writer, "EnableKeepTogether", ro.ObjectFormat.EnableKeepTogether.ToString());
			WriteAttributeString(writer, "EnableSuppress", ro.ObjectFormat.EnableSuppress.ToString());
			WriteAttributeString(writer, "HorizontalAlignment", ro.ObjectFormat.HorizontalAlignment.ToString());



			writer.WriteEndElement();
		}

		private void GetSectionFormat(Section section, ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("SectionFormat");

			WriteAttributeString(writer, "CssClass", section.SectionFormat.CssClass);
			WriteAttributeString(writer, "EnableKeepTogether", section.SectionFormat.EnableKeepTogether.ToString());
			WriteAttributeString(writer, "EnableNewPageAfter", section.SectionFormat.EnableNewPageAfter.ToString());
			WriteAttributeString(writer, "EnableNewPageBefore", section.SectionFormat.EnableNewPageBefore.ToString());
			WriteAttributeString(writer, "EnablePrintAtBottomOfPage", section.SectionFormat.EnablePrintAtBottomOfPage.ToString());
			WriteAttributeString(writer, "EnableResetPageNumberAfter", section.SectionFormat.EnableResetPageNumberAfter.ToString());
			WriteAttributeString(writer, "EnableSuppress", section.SectionFormat.EnableSuppress.ToString());
			WriteAttributeString(writer, "EnableSuppressIfBlank", section.SectionFormat.EnableSuppressIfBlank.ToString());
			WriteAttributeString(writer, "EnableUnderlaySection", section.SectionFormat.EnableUnderlaySection.ToString());

			CRReportDefModel.Section rdm_ro = GetRASRDMSectionObjectFromCRENGSectionObject(section.Name, report);
			if (rdm_ro != null)
            {
                GetSectionAreaFormatConditionFormulas(rdm_ro, writer);
            }


            if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
            {
                GetColorFormat(section.SectionFormat.BackgroundColor, writer, "BackgroundColor");
            }

            writer.WriteEndElement();
		}

		private void GetReportDefinition(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("ReportDefinition");

			GetAreas(report, writer);

			writer.WriteEndElement();
		}

		private void GetAreas(ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Areas");

			foreach (Area area in report.ReportDefinition.Areas)
			{
				writer.WriteStartElement("Area");

				WriteAttributeString(writer, "Kind", area.Kind.ToString());
				WriteAttributeString(writer, "Name", area.Name);

				if ((ShowFormatTypes & FormatTypes.AreaFormat) == FormatTypes.AreaFormat)
                {
                    GetAreaFormat(area, report, writer);
                }

                GetSections(area, report, writer);

				writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

		private void GetSections(Area area, ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("Sections");

			foreach (Section section in area.Sections)
			{
				writer.WriteStartElement("Section");

				WriteAttributeString(writer, "Height", section.Height.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Kind", section.Kind.ToString());
				WriteAttributeString(writer, "Name", section.Name);

				if ((ShowFormatTypes & FormatTypes.SectionFormat) == FormatTypes.SectionFormat)
                {
                    GetSectionFormat(section, report, writer);
                }

                GetReportObjects(section, report, writer);

				writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

		private void GetReportObjects(Section section, ReportDocument report, XmlWriter writer)
		{
			writer.WriteStartElement("ReportObjects");

			foreach (ReportObject reportObject in section.ReportObjects)
			{
				writer.WriteStartElement(reportObject.GetType().Name);

				CRReportDefModel.ISCRReportObject rasrdm_ro = GetRASRDMReportObject(reportObject.Name, report);

				WriteAttributeString(writer, "Name", reportObject.Name);
				WriteAttributeString(writer, "Kind", reportObject.Kind.ToString());

				WriteAttributeString(writer, "Top", reportObject.Top.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Left", reportObject.Left.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Width", reportObject.Width.ToString(CultureInfo.InvariantCulture));
				WriteAttributeString(writer, "Height", reportObject.Height.ToString(CultureInfo.InvariantCulture));
				if (reportObject is SubreportObject srobj)
				{
                    WriteAttributeString(writer, "SubreportName", srobj.SubreportName);
					WriteAttributeString(writer, "EnableOnDemand", srobj.EnableOnDemand.ToString(CultureInfo.InvariantCulture));

				}
				else if (reportObject is BoxObject bo)
				{
                    WriteAttributeString(writer, "Bottom", bo.Bottom.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "EnableExtendToBottomOfSection", bo.EnableExtendToBottomOfSection.ToString());
					WriteAttributeString(writer, "EndSectionName", bo.EndSectionName);
					WriteAttributeString(writer, "LineStyle", bo.LineStyle.ToString());
					WriteAttributeString(writer, "LineThickness", bo.LineThickness.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "Right", bo.Right.ToString(CultureInfo.InvariantCulture));
					if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
                    {
                        GetColorFormat(bo.LineColor, writer, "LineColor");
                    }
                }
				else if (reportObject is DrawingObject dobj)
				{
                    WriteAttributeString(writer, "Bottom", dobj.Bottom.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "EnableExtendToBottomOfSection", dobj.EnableExtendToBottomOfSection.ToString());
					WriteAttributeString(writer, "EndSectionName", dobj.EndSectionName);
					WriteAttributeString(writer, "LineStyle", dobj.LineStyle.ToString());
					WriteAttributeString(writer, "LineThickness", dobj.LineThickness.ToString(CultureInfo.InvariantCulture));
					WriteAttributeString(writer, "Right", dobj.Right.ToString(CultureInfo.InvariantCulture));
					if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
                    {
                        GetColorFormat(dobj.LineColor, writer, "LineColor");
                    }
                }
				else if (reportObject is FieldHeadingObject fh)
				{
                    var rasrdmFh = (CRReportDefModel.FieldHeadingObject)rasrdm_ro;
					WriteAttributeString(writer, "FieldObjectName", fh.FieldObjectName);
					WriteAttributeString(writer, "MaxNumberOfLines", rasrdmFh.MaxNumberOfLines.ToString());
					WriteElementString(writer, "Text", fh.Text);

					if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
                    {
                        GetColorFormat(fh.Color, writer);
                    }

                    if ((ShowFormatTypes & FormatTypes.Font) == FormatTypes.Font)
					{
						GetFontFormat(fh.Font, writer);
						GetFontColorConditionFormulas(rasrdmFh.FontColor, writer);
					}
				}
				else if (reportObject is FieldObject fo)
				{
                    var rasrdmFo = (CRReportDefModel.FieldObject)rasrdm_ro;

					if (fo.DataSource != null)
                    {
                        WriteAttributeString(writer, "DataSource", fo.DataSource.FormulaName);
                    }

                    if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
                    {
                        GetColorFormat(fo.Color, writer);
                    }

                    if ((ShowFormatTypes & FormatTypes.Font) == FormatTypes.Font)
					{
						GetFontFormat(fo.Font, writer);
						GetFontColorConditionFormulas(rasrdmFo.FontColor, writer);
					}

				}
				else if (reportObject is TextObject tobj)
				{
                    var rasrdmTobj = (CRReportDefModel.TextObject)rasrdm_ro;

					WriteAttributeString(writer, "MaxNumberOfLines", rasrdmTobj.MaxNumberOfLines.ToString());
					WriteElementString(writer, "Text", tobj.Text);

					if ((ShowFormatTypes & FormatTypes.Color) == FormatTypes.Color)
                    {
                        GetColorFormat(tobj.Color, writer);
                    }

                    if ((ShowFormatTypes & FormatTypes.Font) == FormatTypes.Font)
					{
						GetFontFormat(tobj.Font, writer);
						GetFontColorConditionFormulas(rasrdmTobj.FontColor, writer);
					}
				}

				if ((ShowFormatTypes & FormatTypes.Border) == FormatTypes.Border)
                {
                    GetBorderFormat(reportObject, report, writer);
                }

                if ((ShowFormatTypes & FormatTypes.ObjectFormat) == FormatTypes.ObjectFormat)
                {
                    GetObjectFormat(reportObject, writer);
                }


                if (rasrdm_ro != null)
                {
                    GetObjectFormatConditionFormulas(rasrdm_ro, writer);
                }

                writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

        public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (_report != null && _createdReport)
                {
                    _report.Dispose();
                }

                _report = null;
				_rcd = null;

				if (_oleCompoundFile != null)
				{
					((IDisposable)_oleCompoundFile).Dispose();
					_oleCompoundFile = null;
				}
			}
		}
	}
}
