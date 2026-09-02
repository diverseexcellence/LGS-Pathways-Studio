using LgsImpact.Api.Controllers;
using Xunit;

namespace LgsImpact.Api.Tests.Controllers;

/// <summary>
/// Upload type resolution for the corrective re-import endpoint. Indiana's ILEARN exports are named
/// like "LibertyGroveSchools_Page1_ADA-ZEL_StudentData_150626 PM.csv" — no ILEARN or Checkpoint
/// token — so filename-only detection classified them as demographics and the file had to be
/// renamed by hand before it could be imported. The columns identify it correctly, so they act as
/// the tie-breaker when the filename falls through to the default.
/// </summary>
public class ReimportUploadTypeTests
{
    // Headers taken from the real ADA-ZEL export.
    private static readonly string[] AdaZelHeaders =
    [
        "Test Group", "Test Subject", "Test Grade", "Test Name", "Student Name", "STN", "Student DOB",
        "Gender", "Ethnicity", "Special Education Status", "Identified English Learner Status",
        "Section 504 Status", "Enrolled Grade", "Enrolled School", "Enrolled Corporation",
        "Test Reason", "Test OppNumber", "Date Taken", "Test Completion Date", "Scale Score",
        "Performance Level", "State Percentile Ranking", "Corporation Percentile Ranking",
    ];

    // Headers from the real IXL LevelUp ELA diagnostic.
    private static readonly string[] IxlElaHeaders =
    [
        "STN", "ID", "First name", "Last name", "Teacher(s)", "Grade", "Course(s)", "Gender", "Race",
        "Hispanic ethnicity", "Date of completion", "Language", "Overall ELA score", "Overall ELA tier",
        "Overall reading score", "Overall reading scale score", "Overall reading tier",
    ];

    private static readonly string[] DemographicsHeaders =
    [
        "STUDENTS.Student_Number", "STUDENTS.DOB", "Grade_Level", "Home_Room", "Ethnicity", "ELL", "LunchStatus",
    ];

    [Fact]
    public void AdaZelFile_ClassifiedAsIlearnFromItsColumns_DespiteTheFileName()
    {
        // The bug this exists to prevent: filename-only detection returns "demographics" here.
        Assert.Equal("demographics",
            UploadController.DetectUploadType("LibertyGroveSchools_Page1_ADA-ZEL_StudentData_150626 PM.csv"));

        Assert.Equal("ILEARN", UploadController.ResolveUploadType(
            "LibertyGroveSchools_Page1_ADA-ZEL_StudentData_150626 PM.csv", AdaZelHeaders, null));
    }

    [Fact]
    public void HandRenamedFile_StillResolvesToIlearn()
    {
        // The manual workaround must keep working — the same data is stored under this name today.
        Assert.Equal("ILEARN", UploadController.ResolveUploadType(
            "LibertyGroveSchools_Page1_ADA-ZEL_StudentData_Checkpoint3.csv", AdaZelHeaders, null));
    }

    [Fact]
    public void ExplicitOverride_WinsOverBothFileNameAndColumns()
    {
        Assert.Equal("IREAD", UploadController.ResolveUploadType(
            "LGS ILEARN Checkpoint 1 Data.csv", AdaZelHeaders, "IREAD"));
    }

    [Fact]
    public void FileNameThatAlreadyIdentifiesTheType_IsNotSecondGuessedByColumns()
    {
        // A confident filename wins: the IXL ELA export's columns also mention ELA/reading tiers,
        // but the name is unambiguous and must not be overridden.
        Assert.Equal("IXL", UploadController.ResolveUploadType(
            "IXL-LevelUp-Diagnostic-Results-ELA(in).csv", IxlElaHeaders, null));
    }

    [Fact]
    public void GenuineDemographicsFile_StaysDemographics()
    {
        Assert.Equal("demographics",
            UploadController.ResolveUploadType("Students_export.csv", DemographicsHeaders, null));
    }

    [Fact]
    public void TestPipelineFiles_AreStillSkipped()
    {
        Assert.Equal("__SKIP__", UploadController.ResolveUploadType(
            "TEST_PIPELINE_Students_export.csv", DemographicsHeaders, null));
    }

    [Fact]
    public void AcadienceAloFile_ResolvesFromTheFileName()
    {
        Assert.Equal("Acadience", UploadController.ResolveUploadType(
            "alo_reading_pm_data_2025-2026.csv", ["Date", "Reading Composite Score"], null));
    }

    [Fact]
    public void UnknownFileWithNoUsableColumns_FallsBackToDemographics()
    {
        Assert.Equal("demographics",
            UploadController.ResolveUploadType("mystery.csv", ["Alpha", "Beta"], null));
    }
}

