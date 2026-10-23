using Scada;
using Scada.Data.Adapters;
using Scada.Data.Models;

if (args.Length is not (2 or 3))
    throw new ArgumentException("Usage: DemoSetup <BaseXML directory> <BaseDAT directory> [table name]");

Directory.CreateDirectory(args[1]);
var dataset = new ConfigDataset();
if (args.Length == 3 && !dataset.AllTables.Any(table => table.Name == args[2]))
    throw new ArgumentException("Unknown table: " + args[2]);
foreach (var table in dataset.AllTables)
{
    if (args.Length == 3 && table.Name != args[2])
        continue;
    string source = Path.Combine(args[0], table.FileName);
    if (File.Exists(source))
        table.Load(source);
    // Initialize a known learning account only in the generated demo database.
    if (table == dataset.UserTable)
    {
        var admin = dataset.UserTable.Items.Values.Single(user => user.Name == "admin");
        admin.Password = ScadaUtils.GetPasswordHash(admin.UserID, "12345");
    }
    new BaseTableAdapter { FileName = Path.Combine(args[1], table.FileNameDat) }.Update(table);
    Console.WriteLine($"{table.Name}: {table.ItemCount} rows");
}

if (args.Length == 2 || args[2] == "User")
    Console.WriteLine("Local demo account initialized: admin / 12345");
