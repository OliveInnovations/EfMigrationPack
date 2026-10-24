using Microsoft.EntityFrameworkCore;

namespace TestApp
{
    // Stage 1 of the test model: one table. Run-IntegrationTests.ps1 replaces this file with
    // stages/Model.v2.cs.txt before adding the second migration.

    public class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    public static class TestModel
    {
        public static void Configure(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(widget =>
            {
                widget.ToTable("Widgets");
                widget.Property(w => w.Name).IsRequired().HasMaxLength(100);
            });
        }
    }
}
