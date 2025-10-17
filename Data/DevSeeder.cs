using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VaxSync.Web.Data;
using VaxSync.Web.Models;

public static class DevSeeder
{
    private static readonly Random _rng = new();

    public static async Task SeedAsync(ApplicationDbContext db, int targetStudentCount, int schoolCount)
    {
        // 0) Fast exit if already seeded near target
        var current = await db.Students.CountAsync();
        if (current >= targetStudentCount * 0.95) return;

        // 1) Vaccines catalog (keep it small & plausible for PVAC-like flows)
        // You can refine codes/names later without touching the generator’s shape.
        if (!await db.Vaccines.AnyAsync())
        {
            db.Vaccines.AddRange(new[]
            {
                new Vaccine { Code = "MMR",   Name = "Measles, Mumps, Rubella" },
                new Vaccine { Code = "POLIO", Name = "Polio (IPV)" },
                new Vaccine { Code = "DTaP",  Name = "Diphtheria, Tetanus, Pertussis" },
                new Vaccine { Code = "Tdap",  Name = "Tetanus, Diphtheria, Pertussis (Adolescent)" },
                new Vaccine { Code = "HEPB",  Name = "Hepatitis B" },
                new Vaccine { Code = "VAR",   Name = "Varicella" },
                new Vaccine { Code = "MCV4",  Name = "Meningococcal ACWY" },
                new Vaccine { Code = "HPV",   Name = "Human Papillomavirus" },
            });
            await db.SaveChangesAsync();
        }

        var vaccineMap = await db.Vaccines.ToDictionaryAsync(v => v.Code, v => v.Id);

        // 2) Tiny “schedule hint” table: dose counts by age band (for generator logic only).
        // This is *not* your compliance engine; it just helps the generator pick doses/dates.
        if (!await db.VaccineSchedules.AnyAsync())
        {
            var vs = new List<VaccineSchedule>();
            void Add(string code, string band, int dose, bool catchup) =>
                vs.Add(new VaccineSchedule { VaccineId = vaccineMap[code], AgeRange = band, DoseNumber = dose, CatchUpEligible = catchup });

            // Example bands — tune as you like
            Add("DTaP", "0–6 years", 1, true); Add("DTaP", "0–6 years", 2, true); Add("DTaP", "0–6 years", 3, true); Add("DTaP", "0–6 years", 4, true);
            Add("Tdap", "11–13 yrs", 1, true);
            Add("POLIO", "0–6 years", 1, true); Add("POLIO", "0–6 years", 2, true); Add("POLIO", "0–6 years", 3, true); Add("POLIO", "4–6 years", 4, true);
            Add("MMR", "1–6 years", 1, true); Add("MMR", "4–6 years", 2, true);
            Add("VAR", "1–6 years", 1, true); Add("VAR", "4–6 years", 2, true);
            Add("HEPB", "0–1 years", 1, true); Add("HEPB", "0–1 years", 2, true); Add("HEPB", "0–1 years", 3, true);
            Add("MCV4", "11–18 yrs", 1, true);
            Add("HPV", "11–18 yrs", 1, true); Add("HPV", "11–18 yrs", 2, true);

            db.VaccineSchedules.AddRange(vs);
            await db.SaveChangesAsync();
        }

        // 3) Schools (5). Uses your School model (string Id, Code, Name). :contentReference[oaicite:11]{index=11}
        if (!await db.Schools.AnyAsync())
        {
            var schools = new List<School>(schoolCount);
            for (int i = 1; i <= schoolCount; i++)
            {
                schools.Add(new School
                {
                    Code = $"S-{i:0000}",
                    Name = MakeSchoolName(i)
                });
            }
            db.Schools.AddRange(schools);
            await db.SaveChangesAsync();
        }
        var schoolIds = await db.Schools.Select(s => new { s.Id, s.Code }).ToListAsync();

        // 4) Generate Students in batches to keep memory & transaction time sane.
        int batchSize = 10_000;
        int remaining = targetStudentCount - current;
        while (remaining > 0)
        {
            int take = Math.Min(batchSize, remaining);
            var students = new List<Student>(take);
            var vaccines = new List<StudentVaccine>(take * 6); // rough average

            for (int i = 0; i < take; i++)
            {
                var age = RandomAge();                              // 4–18
                var dob = RandomDOB(age);
                var school = schoolIds[_rng.Next(schoolIds.Count)]; // random school

                var student = new Student
                {
                    SchoolId = school.Id,                           // matches Student.SchoolId (string) :contentReference[oaicite:12]{index=12}
                    FirstName = RandomFirstName(),
                    LastName = RandomLastName(),
                    DateOfBirth = dob,
                    IsCompliant = false,                            // can compute later; set after we add doses if you want. :contentReference[oaicite:13]{index=13}
                };

                // Build a plausible immunization history for this student
                vaccines.AddRange(BuildVaccinationHistory(student, dob, vaccineMap));

                students.Add(student);
            }

            db.Students.AddRange(students);
            await db.SaveChangesAsync();

            // Connect vaccine rows (need StudentId after SaveChanges)
            foreach (var v in vaccines)
            {
                // Each v.Student holds a reference object; set foreign key instead
                v.StudentId = v.Student.Id;  // StudentVaccine.StudentId is string FK :contentReference[oaicite:14]{index=14}
                v.Student = null!;
            }
            db.StudentVaccines.AddRange(vaccines);
            await db.SaveChangesAsync();

            remaining -= take;
        }

        // 5) (Optional) Populate StudentRequiredDose so due/overdue UI can light up later. :contentReference[oaicite:15]{index=15}
        // You can synthesize due dates from DOB + schedule band windows; mark some Completed=true if a matching StudentVaccine exists.

        // 6) Done. You now have a “live” synthetic DB — UI reads via EF, reports print from rows. :contentReference[oaicite:16]{index=16}
    }

    private static IEnumerable<StudentVaccine> BuildVaccinationHistory(Student s, DateTime dob, Dictionary<string, int> vid)
    {
        // We’ll create doses only up to the student’s current age window,
        // randomly miss some (to simulate non-compliance / catch-up).
        var list = new List<StudentVaccine>();

        void Add(string code, int doseNumber, DateTime when)
        {
            // ~15% chance the dose is “missing” to create realistic gaps
            if (_rng.NextDouble() < 0.15) return;
            list.Add(new StudentVaccine
            {
                Student = s,
                VaccineId = vid[code],
                DoseNumber = doseNumber,
                DateGiven = when
            });
        }

        int age = GetAge(dob, DateTime.Today);

        // Infant/early childhood sets (if age allows)
        if (age >= 1)
        {
            Add("HEPB", 1, dob.AddMonths(1 + _rng.Next(0, 2)));
            Add("HEPB", 2, dob.AddMonths(2 + _rng.Next(0, 2)));
            Add("HEPB", 3, dob.AddMonths(6 + _rng.Next(0, 2)));

            Add("MMR", 1, dob.AddYears(1).AddDays(_rng.Next(0, 120)));
            Add("VAR", 1, dob.AddYears(1).AddDays(_rng.Next(0, 120)));
        }
        if (age >= 4)
        {
            // preschool boosters
            Add("DTaP", 3, dob.AddYears(3).AddMonths(_rng.Next(0, 6)));
            Add("POLIO", 3, dob.AddYears(3).AddMonths(_rng.Next(0, 6)));
        }
        if (age >= 5)
        {
            // kindergarten boosters
            Add("MMR", 2, dob.AddYears(5).AddMonths(_rng.Next(0, 6)));
            Add("VAR", 2, dob.AddYears(5).AddMonths(_rng.Next(0, 6)));
            Add("POLIO", 4, dob.AddYears(5).AddMonths(_rng.Next(0, 6)));
            Add("DTaP", 4, dob.AddYears(5).AddMonths(_rng.Next(0, 6)));
        }
        if (age >= 11)
        {
            Add("Tdap", 1, dob.AddYears(11).AddMonths(_rng.Next(0, 6)));
            Add("MCV4", 1, dob.AddYears(11).AddMonths(_rng.Next(0, 6)));
            // HPV: 2-dose series; randomize completion
            Add("HPV", 1, dob.AddYears(12).AddMonths(_rng.Next(0, 6)));
            Add("HPV", 2, dob.AddYears(12).AddMonths(6 + _rng.Next(0, 4)));
        }

        return list;
    }

    // ——— helpers ———

    private static int RandomAge() => _rng.Next(4, 19); // 4–18 inclusive

    private static DateTime RandomDOB(int age)
    {
        var today = DateTime.Today;
        var start = today.AddYears(-(age + 1));
        var end = today.AddYears(-age);
        return RandomDate(start, end);
    }

    private static DateTime RandomDate(DateTime start, DateTime end)
    {
        var range = (end - start).Days;
        return start.AddDays(_rng.Next(range + 1));
    }

    private static int GetAge(DateTime dob, DateTime on) =>
        (on.Year - dob.Year) - (on.Date < dob.Date.AddYears(on.Year - dob.Year) ? 1 : 0);

    private static string MakeSchoolName(int i)
    {
        // Sprinkle a few Spanish names for PR vibe; keep it synthetic.
        string[] baseNames = {
            "Escuela Superior",
            "Escuela Intermedia",
            "Escuela Elemental",
            "Colegio Regional",
            "Academia Municipal"
        };
        string[] surnames = { "Betances", "Hostos", "Marín", "Sotomayor", "Balseiro", "Cordero", "Piñero", "Valentín", "Rivera", "Pérez" };

        var kind = baseNames[_rng.Next(baseNames.Length)];
        var who = surnames[_rng.Next(surnames.Length)];
        return $"{kind} {who} #{i:0000}";
    }

    private static string RandomFirstName()
    {
        string[] options = { "Ana", "Carlos", "María", "Luis", "Sofía", "Javier", "Valeria", "José", "Camila", "Diego",
                             "Paola", "Héctor", "Andrea", "Isabela", "Daniel", "Gabriela", "Angel", "Lucía", "Mateo", "Alexa" };
        return options[_rng.Next(options.Length)];
    }

    private static string RandomLastName()
    {
        string[] options = { "Rivera", "Pérez", "Santiago", "González", "Rodríguez", "Martínez", "Hernández", "López", "Torres", "Vega",
                             "Morales", "Cruz", "Ortiz", "Ramírez", "Figueroa", "Vázquez", "Díaz", "Colón", "Nieves", "Cordero" };
        return options[_rng.Next(options.Length)];
    }
}
