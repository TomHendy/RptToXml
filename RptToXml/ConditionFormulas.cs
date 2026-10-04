using System;
using System.Runtime.ExceptionServices;
using System.Xml;

using CrystalDecisions.CrystalReports.Engine;

using CRDataDefModel = CrystalDecisions.ReportAppServer.DataDefModel;
using CRReportDefModel = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptToXml
{
	public partial class RptDefinitionWriter
	{
		#region Get ReportAppServer Objects

		private CRReportDefModel.ISCRReportObject GetRASRDMReportObject(string oname, ReportDocument report)
		{
			CRReportDefModel.ISCRReportObject reportObj;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				reportObj = subrptClientDoc.ReportDefController.ReportDefinition.FindObjectByName(oname);
			}
			else
			{
				reportObj = _rcd.ReportDefController.ReportDefinition.FindObjectByName(oname);
			}
			return reportObj;
		}

		private CRReportDefModel.Section GetRASRDMSectionObjectFromCRENGSectionObject(string sname, ReportDocument report)
		{
			CRReportDefModel.Section section;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				section = subrptClientDoc.ReportDefController.ReportDefinition.FindSectionByName(sname);
			}
			else
			{
				section = _rcd.ReportDefController.ReportDefinition.FindSectionByName(sname);
			}
			return section;
		}

		// RAS groups are in group-level order (GroupController.Add/Move take the level as the index), same as the
		// engine's DataDefinition.Groups, so callers pair them by index and then check the condition field (CheckRASField).
		// Pairing by condition field alone would be ambiguous: a report can group twice on one field (e.g. Order Date by
		// month, then by day).
		// Group options (and their GroupOptionsConditionFormulas) hang off each RAS Group; keep-together and
		// repeat-header are group *area* settings and are written by GetAreaFormat.
		private CRDataDefModel.Groups GetRASDDMGroups(ReportDocument report)
		{
			CRDataDefModel.Groups groups;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				groups = subrptClientDoc.DataDefController.DataDefinition.Groups;
			}
			else
			{
				groups = _rcd.DataDefController.DataDefinition.Groups;
			}
			return groups;
		}

		private CRReportDefModel.PrintOptions GetRASRDMPrintOptionsObject(string name, ReportDocument report)
		{
			if (report.IsSubreport)
				return null;

			return _rcd.ReportDocument.PrintOptions;
		}

		private CRReportDefModel.ISCRArea GetRASRDMAreaObjectFromCRENGAreaObject(string aname, ReportDocument report)
		{
			// The engine's Area.Name returns the wrapped RAS ISCRArea.Name, so the names match exactly.
			// ReportDefinition has no FindAreaByName, so search its Areas collection.
			CRReportDefModel.Areas areas;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				areas = subrptClientDoc.ReportDefController.ReportDefinition.Areas;
			}
			else
			{
				areas = _rcd.ReportDefController.ReportDefinition.Areas;
			}

			if (areas == null)
			{
				return null;
			}

			foreach (CRReportDefModel.ISCRArea area in areas)
			{
				if (String.Equals(area.Name, aname, StringComparison.Ordinal))
				{
					return area;
				}
			}
			return null;
		}
	
		#endregion Get ReportAppServer Objects

		private static void GetBorderConditionFormulas(CRReportDefModel.ISCRReportObject ro, XmlWriter writer)
		{
			writer.WriteStartElement("BorderConditionFormulas");
			var cfs = Enum.GetValues(typeof(CRReportDefModel.CrBorderConditionFormulaTypeEnum));
			foreach (CRReportDefModel.CrBorderConditionFormulaTypeEnum cf in cfs)
			{
				var formula = ro.Border.ConditionFormulas[cf];

				if (!String.IsNullOrEmpty(formula.Text))
					WriteAttributeString(writer, GetShortEnumName(cf), formula.Text);
			}
			writer.WriteEndElement();
		}

		private void GetPageMarginConditionFormulas(CRReportDefModel.PrintOptions po, XmlWriter writer)
		{
			writer.WriteStartElement("PageMarginConditionFormulas");
			var cfs = Enum.GetValues(typeof(CRReportDefModel.CrPageMarginConditionFormulaTypeEnum));
			foreach (CRReportDefModel.CrPageMarginConditionFormulaTypeEnum cf in cfs)
			{
				var formula = po.PageMargins.PageMarginConditionFormulas[cf];
				if (!String.IsNullOrEmpty(formula.Text))
					WriteAttributeString(writer, GetShortEnumName(cf), formula.Text);
			}
			writer.WriteEndElement();
		}

		private static void GetAreaFormatConditionFormulas(CRReportDefModel.ISCRArea area, XmlWriter writer)
		{
			// Formulas set on a whole area (Section Expert with the area, not one of its sections, selected), e.g. conditional
			// Suppress / New Page Before / Keep Together of "Group Header #1", Clamp Page Footer, visible groups/records per page.
			// Group areas have no separate collection: ISCRGroupAreaFormat inherits this one, and Keep Group Together / Repeat
			// Group Header have no formula type. Same enum (and attribute names) as GetSectionAreaFormatConditionFormulas.
			// The element is only written when there is at least one formula, so areas without any produce no extra output.
			var conditionFormulas = area.Format?.ConditionFormulas;
			if (conditionFormulas == null)
			{
				return;
			}

			bool hasFormula = false;
			foreach (CRReportDefModel.CrSectionAreaFormatConditionFormulaTypeEnum cf in Enum.GetValues(typeof(CRReportDefModel.CrSectionAreaFormatConditionFormulaTypeEnum)))
			{
				var formula = conditionFormulas[cf];
				if (formula == null || String.IsNullOrEmpty(formula.Text))
				{
					continue;
				}

				if (!hasFormula)
				{
					writer.WriteStartElement("AreaConditionFormulas");
					hasFormula = true;
				}
				WriteAttributeString(writer, GetShortEnumName(cf, "crSectionAreaConditionFormulaType"), formula.Text);
			}

			if (hasFormula)
			{
				writer.WriteEndElement();
			}
		}

		private static void GetSectionAreaFormatConditionFormulas(CRReportDefModel.Section ro, XmlWriter writer)
		{
			writer.WriteStartElement("SectionAreaConditionFormulas");
			var cfs = Enum.GetValues(typeof(CRReportDefModel.CrSectionAreaFormatConditionFormulaTypeEnum));

			// Not filtered by Area/Section kind: the designer only lets a formula be set where it applies, so formulas that do
			// not apply to this section are empty and skipped by the non-empty check below (e.g. Clamp Page Footer is an area
			// setting, ISCRAreaFormat.EnableClampPageFooter; ISCRSectionFormat has no such property). A non-empty formula is
			// stored in the report (e.g. set through the SDK) and belongs in a diff. Area-level formulas: GetAreaFormatConditionFormulas.
			foreach (CRReportDefModel.CrSectionAreaFormatConditionFormulaTypeEnum cf in cfs)
			{
				var formula = ro.Format.ConditionFormulas[cf];
				if (!String.IsNullOrEmpty(formula.Text))
					WriteAttributeString(writer, GetShortEnumName(cf, "crSectionAreaConditionFormulaType"), formula.Text);
			}
			writer.WriteEndElement();
		}

		// Groups and sorts are paired with their RAS objects by position. The engine's FieldDefinition.FormulaName returns the
		// FormulaForm of the RAS field it wraps, so a RAS object on another field means that the pairing is wrong. This throws
		// in that case, so that WriteOptional reports it and writes nothing rather than the settings of another group or sort.
		private static void CheckRASField(CRDataDefModel.ISCRField rasField, string formulaName, string rasObjectDescription)
		{
			string rasFormulaName = rasField?.FormulaForm;
			if (!String.Equals(rasFormulaName, formulaName, StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"{rasObjectDescription} is on {rasFormulaName ?? "no field"}");
			}
		}

		// groupDescription names the group in error messages.
		[HandleProcessCorruptedStateExceptions]
		private static void GetGroupOptions(CRDataDefModel.ISCRGroupOptions ddm_go, string groupDescription, XmlWriter writer)
		{
			if (ddm_go == null)
			{
				return;
			}

			writer.WriteStartElement("GroupOptions");

			// each kind of options is tested on its own; their attribute names differ, so they cannot collide
			if (ddm_go is CRDataDefModel.ISCRDateGroupOptions dateOptions)
			{
				WriteAttributeString(writer, "DateCondition", GetShortEnumName(dateOptions.DateCondition));
			}
			if (ddm_go is CRDataDefModel.ISCRBooleanGroupOptions booleanOptions)
			{
				WriteAttributeString(writer, "BooleanCondition", GetShortEnumName(booleanOptions.BooleanCondition));
			}
			var specified = ddm_go as CRDataDefModel.ISCRSpecifiedGroupOptions;
			if (specified != null)
			{
				WriteAttributeString(writer, "UnspecifiedValuesType", GetShortEnumName(specified.UnspecifiedValuesType));
				WriteAttributeString(writer, "UnspecifiedValuesName", specified.UnspecifiedValuesName);
			}

			GetGroupOptionsConditionFormulas(ddm_go, writer);

			if (specified != null && specified.SpecifiedValueFilters != null)
			{
				// specified (named) groups, in their defined order
				writer.WriteStartElement("SpecifiedGroups");
				foreach (CRDataDefModel.Filter filter in specified.SpecifiedValueFilters)
				{
					// read in full before writing; a failed read is reported and leaves the name or text empty
					string name = null;
					string text = null;
					try
					{
						name = filter.Name;
						// free-edited text takes precedence; otherwise the condition is built from the filter items
						text = filter.FreeEditingText;
						if (String.IsNullOrEmpty(text))
						{
							text = filter.ComputeText();
						}
					}
					catch (Exception e)
					{
						Console.Error.WriteLine($"Error reading specified group '{name}' of {groupDescription}, {e.Message}");
					}

					writer.WriteStartElement("SpecifiedGroup");
					WriteAttributeString(writer, "Name", name);
					WriteString(writer, text); // element text so line breaks are literal
					writer.WriteEndElement();
				}
				writer.WriteEndElement();
			}

			writer.WriteEndElement();
		}

		private static void GetGroupOptionsConditionFormulas(CRDataDefModel.ISCRGroupOptions ddm_go, XmlWriter writer)
		{
			writer.WriteStartElement("GroupOptionsConditionFormulas");

			// CrGroupOptionsConditionFormulaTypeEnum values are crSortDirection / crGroupName (no type-name prefix)
			var conditionFormulas = ddm_go.ConditionFormulas;
			if (conditionFormulas != null)
			{
				foreach (CRDataDefModel.CrGroupOptionsConditionFormulaTypeEnum cf in Enum.GetValues(typeof(CRDataDefModel.CrGroupOptionsConditionFormulaTypeEnum)))
				{
					var formula = conditionFormulas[cf];
					if (formula != null && !String.IsNullOrEmpty(formula.Text))
					{
						WriteAttributeString(writer, GetShortEnumName(cf, "cr"), formula.Text);
					}
				}
			}
			writer.WriteEndElement();
		}

		private static void GetFontColorConditionFormulas(CRReportDefModel.FontColor fco, XmlWriter writer)
		{
			writer.WriteStartElement("FontColorConditionFormulas");

			foreach (var fontColorTypeObj in Enum.GetValues(typeof(CRReportDefModel.CrFontColorConditionFormulaTypeEnum)))
			{
				var fontColorType = (CRReportDefModel.CrFontColorConditionFormulaTypeEnum)fontColorTypeObj;

				var cf = fco.ConditionFormulas[fontColorType];

				if (!String.IsNullOrEmpty(cf.Text))
					WriteAttributeString(writer, GetShortEnumName(fontColorType), cf.Text);
			}

			writer.WriteEndElement();
		}

		private static void GetObjectFormatConditionFormulas(CRReportDefModel.ISCRReportObject ro, XmlWriter writer)
		{
			writer.WriteStartElement("ObjectFormatConditionFormulas");

			foreach (var formulaTypeObj in Enum.GetValues(typeof(CRReportDefModel.CrObjectFormatConditionFormulaTypeEnum)))
			{
				var formulaType = (CRReportDefModel.CrObjectFormatConditionFormulaTypeEnum)formulaTypeObj;

				var cf = ro.Format.ConditionFormulas[formulaType];

				if (!String.IsNullOrEmpty(cf.Text))
					WriteAttributeString(writer, GetShortEnumName(formulaType), cf.Text);
			}

            if (ro is CRReportDefModel.PictureObject)
            {
                var ro_p = (CRReportDefModel.PictureObject)ro;
                var cf = ro_p.GraphicLocationFormula;

                if (!String.IsNullOrEmpty(cf.Text))
                    WriteAttributeString(writer, "GraphicLocation", cf.Text);
            }

			writer.WriteEndElement();
		}

		// The engine's DataDefinition.SortFields is a thin wrapper over the RAS DataDefinition.Sorts collection
		// (same items, same order), and the engine hands out a TopBottomNSortField exactly when the RAS sort is an
		// ISCRTopNSort, so the RAS sort is located by its index in the engine collection.  RAS is needed because the
		// engine does not expose WithTies, the TopN condition formulas, or (for Top/Bottom percentage sorts) the direction.
		// Throws when the RAS sort at that index is missing, is not a TopN sort or is on a field other than sortFieldName
		// (see CheckRASField), so that WriteOptional reports it and nothing is written.
		private void GetTopNSort(ReportDocument report, int sortIndex, string sortFieldName, XmlWriter writer)
		{
			CRDataDefModel.Sorts sorts;
			if (report.IsSubreport)
			{
				var subrptClientDoc = _report.ReportClientDocument.SubreportController.GetSubreport(report.Name);
				sorts = subrptClientDoc.DataDefController.DataDefinition.Sorts;
			}
			else
			{
				sorts = _rcd.DataDefController.DataDefinition.Sorts;
			}

			if (sorts == null || sortIndex >= sorts.Count)
			{
				throw new InvalidOperationException($"RAS has no sort at position {sortIndex + 1}");
			}

			var topNSort = sorts[sortIndex] as CRDataDefModel.ISCRTopNSort;
			if (topNSort == null)
			{
				throw new InvalidOperationException($"the RAS sort at position {sortIndex + 1} is not a TopN sort");
			}
			CheckRASField(topNSort.SortField, sortFieldName, $"the RAS sort at position {sortIndex + 1}");

			writer.WriteStartElement("TopNSort");
			WriteAttributeString(writer, "Direction", GetShortEnumName(topNSort.Direction));
			WriteAttributeString(writer, "NIndividualGroups", topNSort.NIndividualGroups.ToString(System.Globalization.CultureInfo.InvariantCulture));
			WriteAttributeString(writer, "DiscardOthers", topNSort.DiscardOthers.ToString());
			WriteAttributeString(writer, "NotInTopBottomName", topNSort.NotInTopBottomName);
			WriteAttributeString(writer, "WithTies", topNSort.WithTies.ToString());
			GetTopNSortClassConditionFormulas(topNSort, writer);
			writer.WriteEndElement();
		}

		private static void GetTopNSortClassConditionFormulas(CRDataDefModel.ISCRTopNSort topNSort, XmlWriter writer)
		{
			writer.WriteStartElement("TopNConditionFormulas");

			var conditionFormulas = topNSort.ConditionFormulas;
			if (conditionFormulas != null)
			{
				foreach (CRDataDefModel.CrTopNConditionFormulaTypeEnum cf in Enum.GetValues(typeof(CRDataDefModel.CrTopNConditionFormulaTypeEnum)))
				{
					var formula = conditionFormulas[cf];
					if (formula != null && !String.IsNullOrEmpty(formula.Text))
					{
						WriteAttributeString(writer, GetShortEnumName(cf, "crTopN"), formula.Text);
					}
				}
			}

			writer.WriteEndElement();
		}

		private static string GetShortEnumName<T>(T enumValue, string prefix = null) where T : struct
		{
			if (prefix == null)
			{
				string typeName = typeof(T).Name;
				prefix = typeName.EndsWith("Enum", StringComparison.OrdinalIgnoreCase)
					? typeName.Substring(0, typeName.Length - 4)
					: typeName;
			}

			string valueString = enumValue.ToString();

			if (valueString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				valueString = valueString.Substring(prefix.Length);

			return valueString;
		}
	}
}
