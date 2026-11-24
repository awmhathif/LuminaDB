using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using LuminaDB.Core;
using LuminaDB.Core.Diagram;
using LuminaDB.Core.Tables;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    internal static class Shapes
    {
        public static TableDesign T(string name, params string[] columns)
        {
            var t = TableDesign.NewTable("college", name);
            t.OriginalName = name;
            foreach (var c in t.Columns) c.OriginalName = c.Name;
            foreach (var spec in columns)
            {
                var parts = spec.Split(':');
                var col = new ColumnDef { Name = parts[0], OriginalName = parts[0], BaseType = parts.Length > 1 ? parts[1] : "varchar", Length = parts.Length > 2 ? parts[2] : parts.Length > 1 ? "" : "50", Nullable = false };
                t.Columns.Add(col);
            }
            return t;
        }

        public static TableDesign Fk(this TableDesign t, string column, string parent, bool nullable = false)
        {
            if (t.Column(column) == null) t.Columns.Add(new ColumnDef { Name = column, OriginalName = column, BaseType = "int", Nullable = nullable });
            else t.Column(column).Nullable = nullable;
            t.ForeignKeys.Add(new ForeignKeyDef { Name = "fk_" + t.Name + "_" + column, Columns = { column }, RefTable = parent, RefColumns = { "id" } });
            t.Indexes.Add(new IndexDef { Name = "ix_" + column, Columns = { column } });
            return t;
        }

        public static TableDesign AsNew(this TableDesign t)
        {
            t.OriginalName = null;
            foreach (var c in t.Columns) c.OriginalName = null;
            return t;
        }

        /// <summary>departments ← courses ← enrolments → students, staff self-reference, plus a loose table.</summary>
        public static List<TableDesign> College() => new List<TableDesign>
        {
            T("departments", "name"),
            T("courses", "title").Fk("department_id", "departments"),
            T("students", "full_name", "email").Fk("mentor_id", "staff", nullable: true),
            T("staff", "full_name").Fk("department_id", "departments").Fk("manager_id", "staff", nullable: true),
            T("enrolments", "grade:char:2").Fk("student_id", "students").Fk("course_id", "courses"),
            T("settings", "k", "v"),
        };
    }

    public class DiagramLayoutTests
    {
        private static List<DiagramTable> Cards(IEnumerable<TableDesign> t) => t.Select(d => new DiagramTable(d)).ToList();

        [Fact]
        public void Parents_sit_left_of_children_and_nothing_overlaps()
        {
            var cards = Cards(Shapes.College());
            DiagramLayout.Arrange(cards);
            DiagramTable C(string n) => cards.Single(c => c.Name == n);

            Assert.True(C("departments").X < C("courses").X);
            Assert.True(C("courses").X < C("enrolments").X);
            Assert.True(C("students").X < C("enrolments").X);
            Assert.True(C("departments").X < C("staff").X);
            Assert.True(C("staff").X < C("students").X);
            // the table with no relations goes after every connected one
            Assert.True(cards.Where(c => c.Name != "settings").All(c => c.Right < C("settings").X));
            for (int i = 0; i < cards.Count; i++)
                for (int j = i + 1; j < cards.Count; j++)
                    Assert.False(DiagramLayout.Overlap(cards[i], cards[j]), cards[i] + " overlaps " + cards[j]);
        }

        [Fact]
        public void Cycles_and_self_references_still_lay_out()
        {
            var a = Shapes.T("a").Fk("b_id", "b");
            var b = Shapes.T("b").Fk("a_id", "a", nullable: true).Fk("parent_id", "b", nullable: true);
            var cards = Cards(new[] { a, b });
            DiagramLayout.Arrange(cards);
            Assert.False(DiagramLayout.Overlap(cards[0], cards[1]));
            Assert.NotEqual(cards[0].X, cards[1].X);
        }

        [Fact]
        public void A_tall_rank_wraps_into_more_columns()
        {
            var many = Enumerable.Range(1, 40).Select(i => Shapes.T("child" + i.ToString("00"), "a", "b", "c", "d").Fk("hub_id", "hub")).ToList();
            many.Add(Shapes.T("hub"));
            var cards = Cards(many);
            DiagramLayout.Arrange(cards);
            Assert.True(cards.Max(c => c.Bottom) <= DiagramLayout.MaxColumnHeight + DiagramLayout.Origin + 200);
            Assert.True(cards.Where(c => c.Name != "hub").Select(c => c.X).Distinct().Count() > 1);
        }

        [Fact]
        public void New_cards_are_placed_beside_the_saved_ones()
        {
            var cards = Cards(Shapes.College());
            DiagramLayout.Arrange(cards);
            foreach (var c in cards) c.Placed = true;
            var extra = new DiagramTable(Shapes.T("rooms", "code"));
            cards.Add(extra);
            DiagramLayout.PlaceNew(cards);
            Assert.True(extra.X > cards.Where(c => c != extra).Max(c => c.Right));
            var spot = DiagramLayout.FreeSpot(cards, 200, 100, 40, 40);
            Assert.DoesNotContain(cards, c => spot.X < c.Right && spot.X + 200 > c.X && spot.Y < c.Bottom && spot.Y + 100 > c.Y);
        }

        [Fact]
        public void Relations_get_crows_feet_and_one_bars()
        {
            var cards = Cards(Shapes.College());
            DiagramLayout.Arrange(cards);
            var rels = DiagramGeometry.Relations(cards);
            Assert.Equal(6, rels.Count);   // courses, students, staff×2, enrolments×2
            var enrolStudent = rels.Single(r => r.Child.Name == "enrolments" && r.Parent.Name == "students");
            Assert.False(enrolStudent.ChildIsOne);
            Assert.False(enrolStudent.ParentOptional);
            Assert.Equal(4, enrolStudent.Points.Count);
            // starts on the FK row of enrolments, ends on the PK row of students
            Assert.Equal(DiagramGeometry.RowCenter(enrolStudent.Child, "student_id"), enrolStudent.Points[0].Y);
            Assert.Equal(DiagramGeometry.RowCenter(enrolStudent.Parent, "id"), enrolStudent.Points[3].Y);
            Assert.Equal(enrolStudent.Child.X, enrolStudent.Points[0].X);        // parent is to the left
            Assert.Equal(enrolStudent.Parent.Right, enrolStudent.Points[3].X);
            Assert.Equal(3 + 1 + 2, enrolStudent.Marks.Count);                  // crow's foot, mandatory bar, two "exactly one" bars
            Assert.Empty(enrolStudent.Circles);

            var mentor = rels.Single(r => r.Child.Name == "students" && r.Parent.Name == "staff");
            Assert.True(mentor.ParentOptional);
            Assert.Single(mentor.Circles);
            Assert.Contains("zero or one staff", mentor.Meaning);

            var self = rels.Single(r => r.Child.Name == "staff" && r.Parent.Name == "staff");
            Assert.True(self.Points.All(p => p.X >= self.Child.Right));
        }

        [Fact]
        public void A_unique_foreign_key_is_one_to_one()
        {
            var person = Shapes.T("person");
            var passport = Shapes.T("passport", "number").Fk("person_id", "person");
            passport.Indexes.Add(new IndexDef { Name = "uq_person", Kind = IndexKind.Unique, Columns = { "person_id" } });
            var cards = Cards(new[] { person, passport });
            DiagramLayout.Arrange(cards);
            var r = DiagramGeometry.Relations(cards).Single();
            Assert.True(r.ChildIsOne);
            Assert.Equal(2 + 2, r.Marks.Count);
            Assert.Contains("at most one passport", r.Meaning);
        }

        [Fact]
        public void Card_size_follows_names_and_rows()
        {
            var small = DiagramGeometry.MeasureTable(Shapes.T("x"));
            var wide = DiagramGeometry.MeasureTable(Shapes.T("x", "a_really_quite_long_column_name_here:varchar:255"));
            var tall = DiagramGeometry.MeasureTable(Shapes.T("x", "a", "b", "c", "d", "e"));
            Assert.Equal(DiagramGeometry.MinWidth, small.Width);
            Assert.True(wide.Width > small.Width);
            Assert.Equal(DiagramGeometry.HeaderHeight + 6 * DiagramGeometry.RowHeight + DiagramGeometry.BottomPad, tall.Height);
            var rows = DiagramGeometry.Rows(Shapes.College()[4]);
            Assert.Equal("PK", rows[0].Badge);
            Assert.Equal("FK", rows.Single(r => r.Name == "student_id").Badge);
            Assert.Equal("char(2)", rows.Single(r => r.Name == "grade").Type);
        }
    }

    public class DiagramRoutingTests
    {
        [Fact]
        public void A_line_goes_round_a_card_that_sits_between_its_ends()
        {
            // departments (left) <- courses (right); staff sits between them at the same height
            var dept = new DiagramTable(Shapes.T("departments", "name")) { X = 40, Y = 40 };
            var staff = new DiagramTable(Shapes.T("staff", "full_name", "email", "phone", "a", "b", "c", "d", "e")) { X = 380, Y = 30 };
            var courses = new DiagramTable(Shapes.T("courses", "title", "code").Fk("department_id", "departments")) { X = 750, Y = 60 };
            var cards = new List<DiagramTable> { dept, staff, courses };
            var r = DiagramGeometry.Relations(cards).Single();
            Assert.Equal(0, DiagramGeometry.Crossings(r.Points, cards, r.Child, r.Parent));
            Assert.Equal(courses.X, r.Points[0].X);
            Assert.Equal(dept.Right, r.Points[r.Points.Count - 1].X);
            Assert.True(r.Points.Count == 6, "expected a detour round staff, got " + string.Join(" ", r.Points));
            for (int i = 0; i + 1 < r.Points.Count; i++)
                Assert.True(r.Points[i].X == r.Points[i + 1].X || r.Points[i].Y == r.Points[i + 1].Y, "not orthogonal");
        }

        [Fact]
        public void With_nothing_in_the_way_the_line_has_one_vertical_run()
        {
            var cards = new List<DiagramTable>
            {
                new DiagramTable(Shapes.T("a")) { X = 40, Y = 40 },
                new DiagramTable(Shapes.T("b").Fk("a_id", "a")) { X = 400, Y = 200 },
            };
            var r = DiagramGeometry.Relations(cards).Single();
            Assert.Equal(4, r.Points.Count);
            Assert.Equal(0, DiagramGeometry.Crossings(r.Points, cards, r.Child, r.Parent));
        }

        [Fact]
        public void Arranged_college_has_no_line_through_a_card()
        {
            var cards = Shapes.College().Select(t => new DiagramTable(t)).ToList();
            DiagramLayout.Arrange(cards);
            foreach (var r in DiagramGeometry.Relations(cards))
                Assert.True(DiagramGeometry.Crossings(r.Points, cards, r.Child, r.Parent) == 0, r.Label + " crosses a card: " + string.Join(" ", r.Points));
        }
    }

    public class DiagramSyncTests
    {
        [Fact]
        public void New_tables_are_created_parents_first()
        {
            var drafts = Shapes.College().Select(t => t.AsNew()).Reverse().ToList();
            var plan = DiagramSync.Plan("college", new List<TableDesign>(), drafts);
            Assert.Empty(plan.Blockers);
            Assert.Equal(6, plan.Creates);
            int At(string table) => plan.Statements.FindIndex(s => s.StartsWith("CREATE TABLE `college`.`" + table + "`"));
            Assert.True(At("departments") < At("courses"));
            Assert.True(At("courses") < At("enrolments"));
            Assert.True(At("students") < At("enrolments"));
            Assert.True(At("staff") < At("students"));
            Assert.Contains("REFERENCES `staff` (`id`)", plan.Statements[At("staff")]);   // the self-reference stays inline
        }

        [Fact]
        public void A_cycle_of_new_tables_adds_one_relation_afterwards()
        {
            var a = Shapes.T("a").Fk("b_id", "b", nullable: true).AsNew();
            var b = Shapes.T("b").Fk("a_id", "a").AsNew();
            var plan = DiagramSync.Plan("college", new List<TableDesign>(), new[] { a, b });
            Assert.Empty(plan.Blockers);
            Assert.Equal(3, plan.Statements.Count);
            Assert.StartsWith("CREATE TABLE `college`.`a`", plan.Statements[0]);
            Assert.DoesNotContain("FOREIGN KEY", plan.Statements[0]);
            Assert.StartsWith("CREATE TABLE `college`.`b`", plan.Statements[1]);
            Assert.StartsWith("ALTER TABLE `college`.`a` ADD CONSTRAINT `fk_a_b_id` FOREIGN KEY", plan.Statements[2]);
            Assert.Contains(plan.Notes, n => n.Contains("point at each other"));
        }

        [Fact]
        public void A_changed_table_becomes_an_alter_and_an_unchanged_one_nothing()
        {
            var live = Shapes.College();
            var edited = live[2].Clone();
            edited.Columns.Add(new ColumnDef { Name = "phone", BaseType = "varchar", Length = "15" });
            var same = live[0].Clone();
            var plan = DiagramSync.Plan("college", live, new[] { edited, same });
            Assert.Empty(plan.Blockers);
            Assert.Equal(1, plan.Alters);
            Assert.Single(plan.Statements);
            Assert.Contains("ADD COLUMN `phone` VARCHAR(15)", plan.Statements[0]);
            Assert.Contains("5 other tables", string.Join(" ", plan.Notes));

            Assert.True(DiagramSync.Plan("college", live, new[] { same }).IsEmpty);
            Assert.Equal("The database already matches the diagram.", DiagramSync.Plan("college", live, new TableDesign[0]).Summary);
        }

        [Fact]
        public void Clashes_missing_targets_and_dangling_drops_are_blockers()
        {
            var live = Shapes.College();
            var clash = Shapes.T("courses").AsNew();
            Assert.Contains(DiagramSync.Plan("college", live, new[] { clash }).Blockers, b => b.Contains("already exists"));

            var orphan = Shapes.T("rooms").Fk("block_id", "blocks").AsNew();
            Assert.Contains(DiagramSync.Plan("college", live, new[] { orphan }).Blockers, b => b.Contains("points at blocks"));

            var dropParent = DiagramSync.Plan("college", live, new TableDesign[0], new[] { "departments" });
            Assert.Contains(dropParent.Blockers, b => b.StartsWith("courses still points at"));
            Assert.False(dropParent.CanRun);

            var dropBoth = DiagramSync.Plan("college", live, new TableDesign[0], new[] { "students", "enrolments" });
            Assert.Empty(dropBoth.Blockers);
            Assert.Equal(new[] { "DROP TABLE `college`.`enrolments`", "DROP TABLE `college`.`students`" }, dropBoth.Statements);
        }

        [Fact]
        public void A_draft_whose_table_vanished_is_created_instead()
        {
            var live = Shapes.College().Where(t => t.Name != "settings").ToList();
            var draft = Shapes.T("settings", "k", "v");
            var plan = DiagramSync.Plan("college", live, new[] { draft });
            Assert.Equal(1, plan.Creates);
            Assert.Contains(plan.Notes, n => n.Contains("no longer in the database"));
        }

        [Fact]
        public void Add_relation_makes_the_column_index_and_key()
        {
            var students = Shapes.T("students", "name");
            var classes = Shapes.T("classes", "title");
            classes.Column("id").Unsigned = true;
            Assert.Null(DiagramSync.AddRelation(students, classes));
            var col = students.Column("class_id");
            Assert.NotNull(col);
            Assert.True(col.Unsigned);
            Assert.False(col.Nullable);
            var fk = students.ForeignKeys.Single();
            Assert.Equal("fk_students_classes", fk.Name);
            Assert.Equal("classes", fk.RefTable);
            Assert.Equal(new[] { "id" }, fk.RefColumns);
            Assert.Contains(students.Indexes, i => i.Columns.SequenceEqual(new[] { "class_id" }));

            Assert.Null(DiagramSync.AddRelation(students, classes, "second_class_id", required: false));
            Assert.Equal("fk_students_classes_2", students.ForeignKeys[1].Name);

            var mismatch = Shapes.T("x", "class_id:varchar:10");
            Assert.Contains("must match", DiagramSync.AddRelation(mismatch, classes));
            var nokey = Shapes.T("y");
            nokey.Indexes.Clear();
            Assert.Contains("no primary key", DiagramSync.AddRelation(students, nokey));
            Assert.Equal("category", DiagramSync.Singular("categories"));
            Assert.Equal("class", DiagramSync.Singular("classes"));
        }

        [Fact]
        public void Create_script_is_a_complete_hand_in_file()
        {
            var sql = DiagramSync.CreateScript("college", Shapes.College(), includeCreateDatabase: true);
            Assert.Contains("CREATE DATABASE IF NOT EXISTS `college`", sql);
            Assert.Contains("USE `college`;", sql);
            Assert.Equal(6, sql.Split(new[] { "CREATE TABLE" }, StringSplitOptions.None).Length - 1);
            Assert.True(sql.IndexOf("`departments`", StringComparison.Ordinal) < sql.IndexOf("CREATE TABLE `college`.`courses`", StringComparison.Ordinal));
        }
    }

    public class DiagramFileTests
    {
        private static DiagramScene Scene()
        {
            var cards = Shapes.College().Select(t => new DiagramTable(t)).ToList();
            DiagramLayout.Arrange(cards);
            cards[1].State = DiagramTableState.New;
            return new DiagramScene("college — 6 tables", cards);
        }

        [Fact]
        public void Svg_is_well_formed_and_holds_every_table_and_relation()
        {
            var svg = DiagramSvg.Render(Scene());
            var doc = XDocument.Parse(svg);
            XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.Equal("svg", doc.Root.Name.LocalName);
            var texts = doc.Descendants(ns + "text").Select(t => t.Value).ToList();
            foreach (var t in new[] { "departments", "courses", "students", "staff", "enrolments", "settings", "student_id", "char(2)" })
                Assert.Contains(texts, x => x.StartsWith(t));
            Assert.Equal(6, doc.Descendants(ns + "polyline").Count());
            Assert.Contains(doc.Descendants(ns + "polyline"), p => (string)p.Attribute("stroke-dasharray") != null);   // optional relation
            Assert.Contains("#2E7D4F", svg);   // a new table's edge
        }

        [Fact]
        public void Svg_escapes_names_and_keeps_unicode()
        {
            var t = Shapes.T("ශිෂ්‍යයෝ", "a<b&c");
            var svg = DiagramSvg.Render(new DiagramScene("x & y", new[] { new DiagramTable(t) }));
            var doc = XDocument.Parse(svg);
            Assert.Contains("ශිෂ්‍යයෝ", svg);
            Assert.Contains(doc.Descendants().Where(e => e.Name.LocalName == "text"), e => e.Value == "a<b&c");
        }

        [Fact]
        public void Pdf_has_a_valid_structure_and_inflates_to_drawing_commands()
        {
            var bytes = DiagramPdf.Render(Scene());
            var head = Encoding.ASCII.GetString(bytes, 0, 8);
            Assert.Equal("%PDF-1.4", head);
            var text = Encoding.GetEncoding(28591).GetString(bytes);
            Assert.EndsWith("%%EOF\n", text);

            // xref offsets point at the objects
            int sx = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
            long xref = long.Parse(text.Substring(sx + 10).Split('\n')[0]);
            Assert.StartsWith("xref", text.Substring((int)xref));
            var entries = text.Substring((int)xref).Split('\n').Skip(3).Take(7).ToList();
            for (int i = 0; i < 7; i++)
            {
                int off = int.Parse(entries[i].Substring(0, 10));
                Assert.StartsWith((i + 1) + " 0 obj", text.Substring(off));
            }

            // the content stream is zlib: inflate it (skip the 2-byte header) and look for the tables
            int start = text.IndexOf("stream\n", StringComparison.Ordinal) + 7;
            int end = text.IndexOf("\nendstream", StringComparison.Ordinal);
            var z = bytes.Skip(start).Take(end - start).ToArray();
            Assert.Equal(0x78, z[0]);
            string content;
            using (var ms = new MemoryStream(z, 2, z.Length - 6))
            using (var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var r = new StreamReader(d, Encoding.ASCII)) content = r.ReadToEnd();
            Assert.Contains("(enrolments) Tj", content);
            Assert.Contains("(student_id) Tj", content);
            Assert.Contains(" re B", content);
            Assert.True(content.Split(new[] { " c " }, StringSplitOptions.None).Length > 1);   // the optional-relation circle
        }

        [Fact]
        public void Pdf_strings_escape_and_replace_what_courier_cannot_print()
        {
            Assert.Equal(@"a\(b\)\\c", DiagramPdf.PdfString(@"a(b)\c"));
            Assert.Equal("?????", DiagramPdf.PdfString("ශිෂ්ය"));
            Assert.Equal(@"caf\351", DiagramPdf.PdfString("café"));
        }

        [Fact]
        public void Document_round_trips_positions_drafts_and_drops()
        {
            var root = Path.Combine(Path.GetTempPath(), "LuminaDG_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var paths = new AppPaths(root);
                var doc = DiagramDocument.Load(paths, "college");
                Assert.False(doc.HasPendingChanges);
                doc.SetPosition("students", 120.04, 80);
                doc.Drafts.Add(Shapes.T("rooms", "code").Fk("building_id", "buildings").AsNew());
                var edited = Shapes.T("courses", "title");
                edited.Columns.Add(new ColumnDef { Name = "credits", BaseType = "tinyint", Unsigned = true, Nullable = false, Default = "3" });
                doc.Drafts.Add(edited);
                doc.Drops.Add("settings");
                doc.Hidden.Add("logs");
                doc.Save(paths);
                Assert.True(File.Exists(DiagramDocument.PathFor(paths, "college")));

                var back = DiagramDocument.Load(paths, "college");
                Assert.Equal(120, back.PositionOf("STUDENTS").X);
                Assert.Equal(2, back.Drafts.Count);
                var rooms = back.DraftOf("rooms");
                Assert.Null(rooms.OriginalName);
                Assert.Equal("college", rooms.Database);
                Assert.Equal("buildings", rooms.ForeignKeys.Single().RefTable);
                Assert.Equal(IndexKind.Primary, rooms.PrimaryKey.Kind);
                var credits = back.DraftForExisting("courses").Column("credits");
                Assert.True(credits.Unsigned);
                Assert.Equal("3", credits.Default);
                Assert.True(back.IsDropped("Settings"));
                Assert.True(back.IsHidden("logs"));

                // compose: live tables overlaid by drafts, new drafts added, hidden left out, positions applied
                var live = Shapes.College();
                live.Add(Shapes.T("logs", "line"));
                var shown = back.Compose(live);
                Assert.DoesNotContain(shown, t => t.Name == "logs");
                Assert.Equal(DiagramTableState.Changed, shown.Single(t => t.Name == "courses").State);
                Assert.Equal(DiagramTableState.New, shown.Single(t => t.Name == "rooms").State);
                Assert.Equal(DiagramTableState.Dropped, shown.Single(t => t.Name == "settings").State);
                Assert.True(shown.Single(t => t.Name == "students").Placed);
                Assert.False(shown.Single(t => t.Name == "courses").Placed);
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Fact]
        public void A_corrupt_or_partial_file_loads_as_empty_lists()
        {
            var root = Path.Combine(Path.GetTempPath(), "LuminaDG_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var paths = new AppPaths(root);
                var file = DiagramDocument.PathFor(paths, "a/b");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Assert.EndsWith("a_b.json", file);
                File.WriteAllText(file, "{\"Version\":1}");
                var d = DiagramDocument.Load(paths, "a/b");
                Assert.NotNull(d.Positions); Assert.NotNull(d.Drafts); Assert.NotNull(d.Drops); Assert.NotNull(d.Hidden);
                File.WriteAllText(file, "{not json");
                Assert.Empty(DiagramDocument.Load(paths, "a/b").Positions);
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }

    public class DiagramIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public DiagramIntegrationTests(ITestOutputHelper output) { _out = output; }
        public static IEnumerable<object[]> Engines => MariaDbServiceIntegrationTests.Engines;

        private static async Task Exec(MySqlConnection c, string sql)
        {
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync();
        }

        private static async Task Run(MySqlConnection c, SyncPlan plan)
        {
            Assert.True(plan.CanRun, string.Join(" | ", plan.Blockers));
            foreach (var s in plan.Statements) await Exec(c, s);
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Reverse_forward_and_sync_against_a_real_server(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3486);
                await s.StartAsync();
                using (var c = await s.OpenConnectionAsync())
                {
                    await Exec(c, "CREATE DATABASE college");

                    // forward engineer: the whole college schema drawn on an empty diagram (includes a self-reference)
                    var drawn = Shapes.College().Select(t => t.AsNew()).ToList();
                    var create = DiagramSync.Plan("college", new List<TableDesign>(), drawn);
                    await Run(c, create);
                    _out.WriteLine(version + ": " + create.Summary);

                    // reverse engineer it back: same tables, same relations
                    var live = await DiagramReader.ReadAsync(c, "college");
                    Assert.Equal(new[] { "courses", "departments", "enrolments", "settings", "staff", "students" }, live.Select(t => t.Name));
                    var cards = live.Select(t => new DiagramTable(t)).ToList();
                    DiagramLayout.Arrange(cards);
                    var rels = DiagramGeometry.Relations(cards);
                    Assert.Equal(6, rels.Count);
                    Assert.True(rels.Single(r => r.Child.Name == "students").ParentOptional);
                    for (int i = 0; i < cards.Count; i++)
                        for (int j = i + 1; j < cards.Count; j++) Assert.False(DiagramLayout.Overlap(cards[i], cards[j]));

                    // nothing to do now
                    Assert.True(DiagramSync.Plan("college", live, live.Select(t => t.Clone()).ToList()).IsEmpty);

                    // a cycle of new tables plus an edit: a new column with a relation, and a rename of a referenced table
                    var a = Shapes.T("rooms", "code").AsNew();
                    a.Database = "college";
                    var b = Shapes.T("buildings", "name").AsNew();
                    b.Database = "college";
                    Assert.Null(DiagramSync.AddRelation(a, b));
                    Assert.Null(DiagramSync.AddRelation(b, a, "main_room_id", required: false));
                    var courses = live.Single(t => t.Name == "courses").Clone();
                    Assert.Null(DiagramSync.AddRelation(courses, a, "room_id", required: false));
                    var departments = live.Single(t => t.Name == "departments").Clone();
                    departments.Name = "faculties";
                    var others = live.Where(t => t.Name != "courses" && t.Name != "departments").Select(t => t.Clone()).ToList();
                    DiagramSync.RenameReferences(new[] { courses }.Concat(others), "departments", "faculties");

                    var change = DiagramSync.Plan("college", live, new[] { a, b, courses, departments });
                    foreach (var st in change.Statements) _out.WriteLine("  " + st.Replace("\n", " "));
                    await Run(c, change);
                    Assert.Equal(2, change.Creates);
                    Assert.Equal(2, change.Alters);

                    var after = await DiagramReader.ReadAsync(c, "college");
                    Assert.Contains(after, t => t.Name == "faculties");
                    Assert.DoesNotContain(after, t => t.Name == "departments");
                    Assert.Equal("faculties", after.Single(t => t.Name == "courses").ForeignKeys.Single(f => f.Columns[0] == "department_id").RefTable);
                    Assert.Equal("rooms", after.Single(t => t.Name == "courses").ForeignKeys.Single(f => f.Columns[0] == "room_id").RefTable);
                    Assert.Equal("rooms", after.Single(t => t.Name == "buildings").ForeignKeys.Single().RefTable);
                    Assert.Equal(9, DiagramGeometry.Relations(after.Select(t => new DiagramTable(t)).ToList()).Count);

                    // drop children before parents, in one plan
                    var drop = DiagramSync.Plan("college", after, new TableDesign[0], new[] { "enrolments", "students" });
                    await Run(c, drop);
                    var final = await DiagramReader.ReadAsync(c, "college");
                    Assert.DoesNotContain(final, t => t.Name == "students" || t.Name == "enrolments");

                    // the hand-in script recreates the schema in another database
                    var script = DiagramSync.CreateScript("college_copy", final, includeCreateDatabase: false);
                    await Exec(c, "CREATE DATABASE college_copy");
                    await Exec(c, script);
                    var copy = await DiagramReader.ReadAsync(c, "college_copy");
                    Assert.Equal(final.Select(t => t.Name), copy.Select(t => t.Name));
                    Assert.Equal(final.Sum(t => t.ForeignKeys.Count), copy.Sum(t => t.ForeignKeys.Count));

                    // export files of the real schema
                    var scene = new DiagramScene("college", final.Select(t => new DiagramTable(t)).ToList());
                    DiagramLayout.Arrange(scene.Tables);
                    XDocument.Parse(DiagramSvg.Render(new DiagramScene("college", scene.Tables)));
                    Assert.True(DiagramPdf.Render(new DiagramScene("college", scene.Tables)).Length > 500);
                }
                await s.StopAsync();
            }
        }
    }
}
