using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Scripting;
using Recipes;
using WorkspaceServer.Models.Completion;
using WorkspaceServer.Models.Execution;

namespace WorkspaceServer.Servers.Scripting
{
    public class ScriptingWorkspaceServer : IWorkspaceServer
    {
        public async Task<RunResult> Run(RunRequest request)
        {
            var source = SourceFile.Create(request.RawSource);

            var options = ScriptOptions.Default
                                       .AddReferences(GetReferenceAssemblies())
                                       .AddImports(GetDefultUsings());

            ScriptState<object> state = null;
            var variables = new Dictionary<string, Variable>();
            Exception exception = null;

            using (var console = new RedirectConsoleOutput())
            {
                try
                {
#if true
                    var sourceLines = source.Text.Lines;

                    var buffer = new StringBuilder();

                    foreach (var sourceLine in sourceLines)
                    {
                        buffer.AppendLine(sourceLine.ToString());

                        // Convert from 0-based to 1-based.
                        var lineNumber = sourceLine.LineNumber + 1;

                        try
                        {
                            console.Clear();

                            state = await (state?.ContinueWithAsync(buffer.ToString(),
                                                                    catchException: ex => true) ??
                                           CSharpScript.RunAsync(
                                               buffer.ToString(),
                                               options));

                            foreach (var scriptVariable in state.Variables)
                            {
                                variables.GetOrAdd(scriptVariable.Name,
                                                   name => new Variable(name))
                                         .TryAddState(
                                             new VariableState(
                                                 lineNumber,
                                                 scriptVariable.Value,
                                                 scriptVariable.Type));
                            }
                        }
                        catch (CompilationErrorException)
                        {
                            if (lineNumber == sourceLines.Count)
                            {
                                throw;
                            }
                        }
                        catch (Exception ex)
                        {
                            exception = ex;
                            break;
                        }
                    }

#else
                    state = await
                                CSharpScript.RunAsync(
                                    request.RawSource,
                                    options);
#endif
                }
                catch (CompilationErrorException compilationErrorException)
                {
                    return new RunResult(
                        false,
                        compilationErrorException.Diagnostics
                                                 .Select(d => d.ToString())
                                                 .ToArray());
                }

                return new RunResult(
                    succeeded: exception == null,
                    output: console.ToString()
                                   .Replace("\r\n", "\n")
                                   .Split('\n'),
                    returnValue: state?.ReturnValue,
                    exception: exception?.ToString() ?? state?.Exception?.ToString(),
                    variables: variables.Values);
            }
        }

        private static Assembly[] GetReferenceAssemblies()
        {
            return new[]
            {
                typeof(object).GetTypeInfo().Assembly,
                typeof(Enumerable).GetTypeInfo().Assembly,
                typeof(Console).GetTypeInfo().Assembly
            };
        }

        private static string[] GetDefultUsings()
        {
            return new[] { "System", "System.Linq", "System.Collections.Generic" };
        }

        private static void Compile(Script script)
        {
            var compilation = script.GetCompilation();

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);

                if (!result.Success)
                {
                    var failures = result.Diagnostics
                                         .Where(diagnostic =>
                                                    diagnostic.IsWarningAsError ||
                                                    diagnostic.Severity == DiagnosticSeverity.Error);

                    foreach (var diagnostic in failures)
                    {
                        Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }
                }
                else
                {
                    ms.Seek(0, SeekOrigin.Begin);

                    var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
                }
            }
        }

        public async Task<CompletionResult> GetCompletionList(CompletionRequest request)
        {
            var sourceFile = SourceFile.Create(request.RawSource, request.Position);

            var document = CreateDocument(sourceFile);
            var service = Microsoft.CodeAnalysis.Completion.CompletionService.GetService(document);

            var completionList = await service.GetCompletionsAsync(document, request.Position);

            return new CompletionResult(
                items: completionList.Items.Select(item => item.ToModel()).ToArray());
        }

        private static Document CreateDocument(SourceFile sourceFile)
        {
            var workspace = new AdhocWorkspace(MefHostServices.DefaultHost);

            var projectId = ProjectId.CreateNewId("ScriptProject");

            var metadataReferences = GetReferenceAssemblies()
                .Select(a => MetadataReference.CreateFromFile(a.Location));

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: GetDefultUsings());

            var projectInfo = ProjectInfo.Create(
                projectId,
                version: VersionStamp.Create(),
                name: "ScriptProject",
                assemblyName: "ScriptProject",
                language: LanguageNames.CSharp,
                compilationOptions: compilationOptions,
                metadataReferences: metadataReferences);

            workspace.AddProject(projectInfo);

            var documentId = DocumentId.CreateNewId(projectId, "ScriptDocument");

            var documentInfo = DocumentInfo.Create(documentId,
                name: "ScriptDocument",
                sourceCodeKind: SourceCodeKind.Script);

            workspace.AddDocument(documentInfo);

            var solution = workspace.CurrentSolution
                .WithDocumentText(documentId, sourceFile.Text);

            workspace.TryApplyChanges(solution);

            return workspace.CurrentSolution.GetDocument(documentId);
        }
    }
}