# RptToXml

Dumps a Crystal Reports RPT file to XML. Useful for diffs.

Binary releases available on the [Releases](https://github.com/ajryan/RptToXml/releases) page.

Ported to C# from the [original VB project](http://code.google.com/p/rpttoxml/)

## Running

Download the latest [release](https://github.com/ajryan/RptToXml/releases).

RptToXml references Crystal Reports assemblies. The easiest way to get them onto a development machine is to install the Crystal Reports Runtime from an MSI downloaded from [this page](https://www.sap.com/cmp/td/sap-crystal-reports-visual-studio-trial.html).

Install the SAP frameworks (probably need 32-bit and 64-bit):

- SAP Crystal Reports for Visual Studio (SP##) runtime engine for .NET framework MSI (32-bit)
- SAP Crystal Reports for Visual Studio (SP##) runtime engine for .NET framework MSI (64-bit)

Run the executable from the command line with

```sh
# process a single file
RptToXml.exe path/to/report_name.rpt path/to/output.xml

# process a directory of reports
cd path/to/reports
RptToXml.exe -r
```

`--ignore-errors` keeps going after a report fails to convert. The exit code is non-zero if any report failed, with or without it. Errors are written to standard error.

### Using with `git diff`

RptToXml can be a git [textconv](https://git-scm.com/docs/gitattributes#_performing_text_diffs_of_binary_files) driver, so `git diff` and `git log -p` show changes to `.rpt` files as XML differences. With `RptToXml.exe` on the `PATH`, run this in the repository that holds the reports:

```sh
git config diff.rpt.textconv "RptToXml.exe --stdout"
```

and add this line to its `.gitattributes` file:

```
*.rpt diff=rpt
```

`--stdout` writes the XML to standard output as UTF-8 and sends errors to standard error, so they don't end up in the diff. It also leaves out the `FileName` attribute, which would show the temporary copy that git passes instead of the report's path. If a version of a report cannot be converted, git stops with "unable to read files to diff".

## Building From Source

The solution targets .NET Framework 4.8.1 and uses C# 7, so it needs Visual Studio 2022 with the .NET Framework 4.8.1 targeting pack.

Install the SAP package for Visual Studio:

- SAP Crystal Reports for Visual Studio (SP##) installation package for Microsoft Visual Studio IDE (VS 20## and above or below)


Find the executable `RptToXml.exe` in ```RptToXml/bin/<where did you build to?>``` after building the solution in Visual Studio.

To check a build against an earlier one, run [scripts/Compare-Samples.ps1](scripts/Compare-Samples.ps1) on a machine with the Crystal Reports runtime. It converts copies of the reports in `RptToXml/Samples` with both builds and checks the new one: every report the old build converts must still convert, to well-formed XML, with the same bytes in two runs and, apart from the `FileName` attribute, with `--stdout`. It then lists how the new XML differs from the old one (`summary.txt` in the output folder, plus a text diff when git is on the `PATH`). Use a build of the commit you started from as the old build:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Compare-Samples.ps1 -Baseline path\to\old\RptToXml.exe -Candidate RptToXml\bin\Debug\RptToXml.exe
```
