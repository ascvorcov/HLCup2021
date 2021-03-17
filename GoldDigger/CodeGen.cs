namespace GoldDigger
{
	using System;
	using System.IO;
	using System.Threading.Tasks;
	using NSwag;
	using NSwag.CodeGeneration.CSharp;

	public class CodeGen
	{
		public static async Task Run()
		{
			System.Net.WebClient wclient = new System.Net.WebClient();

			var document = await OpenApiDocument.FromJsonAsync(File.ReadAllText("nswag.json"));

			wclient.Dispose();

			var settings = new CSharpClientGeneratorSettings
			{
				ClassName = "MyClass",
				CSharpGeneratorSettings =
				{
					Namespace = "MyNamespace"
				}
			};

			var generator = new CSharpClientGenerator(document, settings);
			var code = generator.GenerateFile();
			Console.WriteLine(code);
		}
	}
}
