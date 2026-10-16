using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DataImportTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext ReadOnly = new("rae@example.com", ["Reader"]);

    public DataImportTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Lead", "autoname": "CON-.####", "fields": [
                { "name": "full_name", "type": "Data", "label": "Full Name", "required": true },
                { "name": "email", "type": "Email", "unique": true },
                { "name": "age", "type": "Int" }, { "name": "balance", "type": "Currency" },
                { "name": "joined", "type": "Date" }, { "name": "active", "type": "Check" },
                { "name": "tier", "type": "Select", "options": "Gold\nSilver" }, { "name": "notes", "type": "Text" },
                { "name": "total", "type": "Currency", "formula": "balance * 2" }, { "name": "pin", "type": "Password" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Reader", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Named Thing", "autoname": "prompt", "fields": [ { "name": "label", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private Task<ImportResult> ImportAsync(string csv, string mode = "Insert", bool stop = false, UserContext? user = null, string doctype = "Lead") =>
        _engine.DataImport.RunAsync(doctype, csv, mode, stop, user ?? Ann);

    private async Task<List<Dictionary<string, object?>>> ContactsAsync() =>
        (await _engine.Documents.ListAsync("Lead", new ListRequest { OrderBy = "creation", Descending = false, Limit = 100 }, Ann)).Data.ToList();

    // ---- reading CSV ----

    [Theory]
    [InlineData("a,b\r\n1,2\r\n", "a|b;1|2")]
    [InlineData("a,b\n1,2", "a|b;1|2")]
    [InlineData("﻿a,b\n1,2\n", "a|b;1|2")]
    [InlineData("a,b\n\"x,y\",\"say \"\"hi\"\"\"\n", "a|b;x,y|say \"hi\"")]
    [InlineData("a\n\"line1\nline2\"\n", "a;line1\nline2")]
    [InlineData("a,b\n\n1,\n", "a|b;1|")]
    [InlineData(",\n", "|")]
    public void Csv_is_read_as_spreadsheets_write_it(string csv, string expected) =>
        Assert.Equal(expected, string.Join(';', CsvReader.Read(csv).Select(r => string.Join('|', r))));

    [Fact]
    public void An_unclosed_quote_or_a_huge_file_is_refused()
    {
        Assert.Throws<FormatException>(() => CsvReader.Read("a,b\n\"never closed,2\n"));
        Assert.Throws<FormatException>(() => CsvReader.Read(string.Join('\n', Enumerable.Repeat(string.Join(',', Enumerable.Repeat("x", 100)), CsvReader.MaxCells / 100 + 2))));
    }

    // ---- importing ----

    [Fact]
    public async Task Rows_become_documents_with_every_field_type_converted()
    {
        var result = await ImportAsync("Full Name,email,age,balance,joined,active,tier,notes\r\nAnn Lee,ann@x.com,41,\"1,250.50\",2026-03-04,yes,Gold,\"two\nlines\"\r\nBo,bo@x.com,,10,,no,Silver,\r\n".Replace("\"1,250.50\"", "1250.50"));

        Assert.Equal((2, 0, 0), (result.Created, result.Updated, result.Failed));
        var contacts = await ContactsAsync();
        Assert.Equal(["Ann Lee", "Bo"], contacts.Select(c => (string)c["full_name"]!));
        var ann = contacts[0];
        Assert.Equal((41L, 1250.50m, new DateOnly(2026, 3, 4), true, "Gold"), (Convert.ToInt64(ann["age"]), Convert.ToDecimal(ann["balance"]), (DateOnly)ann["joined"]!, (bool)ann["active"]!, (string)ann["tier"]!));
        Assert.Equal("two\nlines", ann["notes"]);
        Assert.Matches("^CON-\\d+$", (string)ann["name"]!);
        Assert.Equal(Ann.User, ann["owner"]); // made by the person importing
    }

    [Fact]
    public async Task Columns_match_field_names_or_labels_in_any_case_and_unknown_ones_are_ignored()
    {
        var result = await ImportAsync("FULL NAME,Extra,Email\nZed,whatever,z@x.com\n");

        Assert.Equal(1, result.Created);
        var preview = await _engine.DataImport.PreviewAsync("Lead", "FULL NAME,Extra,Email\nZed,whatever,z@x.com\n", "Insert", Ann);
        Assert.Equal(["full_name", null, "email"], preview.Columns.Select(c => c.Field));
    }

    [Fact]
    public async Task A_bad_row_is_reported_with_its_line_and_the_others_still_import()
    {
        var csv = "full_name,age,joined,active,tier,email\nGood,1,2026-01-01,yes,Gold,g@x.com\n,2,2026-01-01,yes,Gold,h@x.com\nBadAge,x,2026-01-01,yes,Gold,i@x.com\nBadDate,1,31/02/2026,yes,Gold,j@x.com\nBadFlag,1,2026-01-01,maybe,Gold,k@x.com\nBadTier,1,2026-01-01,yes,Bronze,l@x.com\nDup,1,2026-01-01,yes,Gold,g@x.com\nLast,1,2026-01-01,no,Silver,m@x.com\n";

        var result = await ImportAsync(csv);

        Assert.Equal((2, 6), (result.Created, result.Failed));
        Assert.Equal([3, 4, 5, 6, 7, 8], result.Errors.Select(e => e.Row).Order().Take(6));
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Field == "full_name");
        Assert.Contains(result.Errors, e => e.Row == 4 && e.Field == "age");
        Assert.Contains(result.Errors, e => e.Row == 6 && e.Field == "active");
        Assert.Equal(["Good", "Last"], (await ContactsAsync()).Select(c => (string)c["full_name"]!));
    }

    [Fact]
    public async Task Stopping_at_the_first_error_leaves_the_rest_alone()
    {
        var result = await ImportAsync("full_name,age\nOne,1\nTwo,x\nThree,3\n", stop: true);

        Assert.Equal((1, 1, true), (result.Created, result.Failed, result.Stopped));
        Assert.Single(await ContactsAsync());
    }

    [Fact]
    public async Task Update_mode_changes_existing_documents_by_their_id()
    {
        await ImportAsync("full_name,age\nOne,1\nTwo,2\n");
        var contacts = await ContactsAsync();

        var result = await ImportAsync($"name,age,tier\n{contacts[0]["name"]},50,Silver\n{contacts[1]["name"]},,\nNOPE,3,\n", "Update");

        Assert.Equal((2, 1), (result.Updated, result.Failed));
        Assert.Equal("No document has this ID.", Assert.Single(result.Errors).Message);
        var after = await ContactsAsync();
        Assert.Equal((50L, "Silver"), (Convert.ToInt64(after[0]["age"]), (string)after[0]["tier"]!));
        Assert.Null(after[1]["age"]); // a blank cell clears the value when updating
    }

    [Fact]
    public async Task A_file_to_update_needs_an_id_column_and_ids_on_every_row()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => ImportAsync("full_name\nx\n", "Update"));
        var preview = await _engine.DataImport.PreviewAsync("Lead", "name,age\n,5\n", "Update", Ann);
        Assert.Contains(preview.Errors, e => e.Field == "name");
    }

    [Fact]
    public async Task Fields_that_are_calculated_passwords_or_ids_cannot_be_set_by_a_file()
    {
        var preview = await _engine.DataImport.PreviewAsync("Lead", "full_name,total,pin,creation,owner\nA,1,2,2000-01-01,someone@x.com\n", "Insert", Ann);
        Assert.Equal(["full_name", null, null, null, null], preview.Columns.Select(c => c.Field));

        await ImportAsync("full_name,total,pin,creation,owner\nA,1,2,2000-01-01,someone@x.com\n");
        var contact = Assert.Single(await ContactsAsync());
        Assert.Equal(Ann.User, contact["owner"]);
        Assert.NotEqual(new DateTime(2000, 1, 1), contact["creation"]);
        Assert.DoesNotContain("total", ImportDataFields());
    }

    private IEnumerable<string> ImportDataFields() => DataImportService.ImportableFields(_engine.Registry.Get("Lead")).Select(f => f.Name);

    [Fact]
    public async Task A_name_column_names_documents_that_are_named_by_typing()
    {
        var result = await ImportAsync("name,label\nAlpha,A\nBeta,B\n", doctype: "Named Thing");

        Assert.Equal(2, result.Created);
        Assert.True(await _engine.Repository.ExistsAsync(_engine.Registry.Get("Named Thing"), "Alpha"));
    }

    [Fact]
    public async Task Text_exported_with_a_leading_apostrophe_comes_back_as_it_was()
    {
        await ImportAsync("full_name,notes\n'=SUM(A1),'+44 20 7946 0000\n");

        var contact = Assert.Single(await ContactsAsync());
        Assert.Equal(("=SUM(A1)", "+44 20 7946 0000"), ((string)contact["full_name"]!, (string)contact["notes"]!));
    }

    // ---- permissions and limits ----

    [Fact]
    public async Task Importing_needs_the_right_to_create_and_updating_the_right_to_write()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ImportAsync("full_name\nx\n", user: ReadOnly));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ImportAsync("name,age\nX,1\n", "Update", user: ReadOnly));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.DataImport.PreviewAsync("Lead", "full_name\nx\n", "Insert", ReadOnly));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Task.FromResult(_engine.DataImport.Template("Lead", "Insert", ReadOnly)));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => ImportAsync("full_name\nx\n", user: UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => ImportAsync("a\n1\n", doctype: "Nope"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ImportAsync("a\n1\n", doctype: "ToDo"));
        Assert.Empty(await ContactsAsync());
    }

    [Fact]
    public async Task Bad_files_and_modes_are_refused_before_anything_is_written()
    {
        async Task<string> Field(string csv, string mode = "Insert") =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => ImportAsync(csv, mode))).Errors.First().Field;

        Assert.Equal("csv", await Field(""));
        Assert.Equal("csv", await Field("nothing,matches\n1,2\n"));
        Assert.Equal("csv", await Field("full_name,full_name\nA,B\n"));
        Assert.Equal("csv", await Field("full_name\n\"open\n"));
        Assert.Equal("csv", await Field("full_name\n" + string.Concat(Enumerable.Repeat("x\n", DataImportService.MaxRows + 1))));
        Assert.Equal("csv", await Field("full_name\n" + new string('x', DataImportService.MaxBytes + 1)));
        Assert.Equal("mode", await Field("full_name\nx\n", "Delete"));
        Assert.Empty(await ContactsAsync());
    }

    [Fact]
    public async Task A_preview_changes_nothing_and_shows_columns_a_sample_and_the_problems()
    {
        var preview = await _engine.DataImport.PreviewAsync("Lead", "full_name,age\nA,1\nB,x\nC,3\nD,4\nE,5\nF,6\n", "Insert", Ann);

        Assert.Equal((6, 5), (preview.Rows, preview.Sample.Count));
        Assert.Equal(["full_name", "age"], preview.Columns.Select(c => c.Field));
        Assert.Equal(3, Assert.Single(preview.Errors).Row);
        Assert.Empty(await ContactsAsync());
    }

    [Fact]
    public async Task The_template_has_the_columns_a_file_may_fill_in()
    {
        var insert = _engine.DataImport.Template("Lead", "Insert", Ann);
        var update = _engine.DataImport.Template("Lead", "Update", Ann);

        Assert.Equal("full_name,email,age,balance,joined,active,tier,notes\r\n", insert);
        Assert.StartsWith("name,full_name,", update);
    }

    [Fact]
    public async Task A_file_cannot_smuggle_values_through_hostile_cells()
    {
        var result = await ImportAsync("full_name,notes\n\"'; DROP TABLE tabContact; --\",<script>alert(1)</script>\n");

        Assert.Equal(1, result.Created);
        var contact = Assert.Single(await ContactsAsync());
        Assert.Equal("'; DROP TABLE tabContact; --", contact["full_name"]); // stored as data, in a parameter
        Assert.Equal("<script>alert(1)</script>", contact["notes"]);
    }
}
