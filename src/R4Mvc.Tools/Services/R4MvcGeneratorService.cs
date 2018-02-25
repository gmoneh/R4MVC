using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using R4Mvc.Tools.CodeGen;
using R4Mvc.Tools.Extensions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace R4Mvc.Tools.Services
{
    public class R4MvcGeneratorService
    {
        private readonly IControllerRewriterService _controllerRewriter;
        private readonly IControllerGeneratorService _controllerGenerator;
        private readonly IStaticFileGeneratorService _staticFileGenerator;
        private readonly IFilePersistService _filePersistService;
        private readonly Settings _settings;

        private static readonly string[] pragmaCodes = { "1591", "3008", "3009", "0108" };

        public const string R4MvcFileName = "R4Mvc.generated.cs";

        private const string _headerText = 
@"// <auto-generated />
// This file was generated by a R4Mvc.
// Don't change it directly as your change would get overwritten.  Instead, make changes
// to the r4mvc.json file (i.e. the settings file), save it and rebuild.

// Make sure the compiler doesn't complain about missing Xml comments and CLS compliance
// 0108: suppress ""Foo hides inherited member Foo.Use the new keyword if hiding was intended."" when a controller and its abstract parent are both processed";

        public R4MvcGeneratorService(
            IControllerRewriterService controllerRewriter,
            IControllerGeneratorService controllerGenerator,
            IStaticFileGeneratorService staticFileGenerator,
            IFilePersistService filePersistService,
            Settings settings)
        {
            _controllerRewriter = controllerRewriter;
            _controllerGenerator = controllerGenerator;
            _staticFileGenerator = staticFileGenerator;
            _filePersistService = filePersistService;
            _settings = settings;
        }

        public void Generate(string projectRoot, IList<ControllerDefinition> controllers)
        {
            var areaControllers = controllers.ToLookup(c => c.Area);

            // Processing controllers, generating partial and derived controller classes for R4Mvc
            var generatedControllers = new List<NamespaceDeclarationSyntax>();
            foreach (var namespaceGroup in controllers.Where(c => c.Namespace != null).GroupBy(c => c.Namespace).OrderBy(c => c.Key))
            {
                var namespaceNode = NamespaceDeclaration(ParseName(namespaceGroup.Key));
                foreach (var controller in namespaceGroup.OrderBy(c => c.Name))
                {
                    namespaceNode = namespaceNode.AddMembers(
                        _controllerGenerator.GeneratePartialController(controller),
                        _controllerGenerator.GenerateR4Controller(controller));

                    // If SplitIntoMultipleFiles is set, store the generated classes alongside the controller files.
                    if (_settings.SplitIntoMultipleFiles)
                    {
                        var controllerFile = NewCompilationUnit()
                            .AddMembers(namespaceNode);
                        CompleteAndWriteFile(controllerFile, controller.GetFilePath().TrimEnd(".cs") + ".generated.cs");
                        namespaceNode = NamespaceDeclaration(ParseName(namespaceGroup.Key));
                    }
                }

                // If SplitIntoMultipleFiles is NOT set, bundle them all in R4Mvc
                if (!_settings.SplitIntoMultipleFiles)
                    generatedControllers.Add(namespaceNode);
            }

            // R4MVC namespace used for the areas and Dummy class
            var r4Namespace = NamespaceDeclaration(ParseName(_settings.R4MvcNamespace))
                // add the dummy class using in the derived controller partial class
                .WithDummyClass()
                .AddMembers(CreateViewOnlyControllerClasses(controllers).ToArray<MemberDeclarationSyntax>())
                .AddMembers(CreateAreaClasses(areaControllers).ToArray<MemberDeclarationSyntax>());

            // create static MVC class and add the area and controller fields
            var mvcStaticClass = ClassDeclaration(_settings.HelpersPrefix)
                .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword)
                .WithGeneratedNonUserCodeAttributes();
            foreach (var area in areaControllers.Where(a => !string.IsNullOrEmpty(a.Key)).OrderBy(a => a.Key))
            {
                mvcStaticClass = mvcStaticClass.WithStaticFieldBackedProperty(area.First().AreaKey, $"{_settings.R4MvcNamespace}.{area.Key}AreaClass", SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword);
            }
            foreach (var controller in areaControllers[string.Empty].OrderBy(c => c.Namespace == null).ThenBy(c => c.Name))
            {
                mvcStaticClass = mvcStaticClass.AddMembers(
                    SyntaxNodeHelpers.CreateFieldWithDefaultInitializer(
                        controller.Name,
                        controller.FullyQualifiedGeneratedName,
                        controller.FullyQualifiedR4ClassName ?? controller.FullyQualifiedGeneratedName,
                        SyntaxKind.PublicKeyword,
                        SyntaxKind.StaticKeyword,
                        SyntaxKind.ReadOnlyKeyword));
            }

            // Generate a list of all static files from the wwwroot path
            var staticFileNode = _staticFileGenerator.GenerateStaticFiles(projectRoot);

            var r4mvcNode = NewCompilationUnit()
                    .AddMembers(
                        mvcStaticClass,
                        r4Namespace,
                        staticFileNode,
                        ActionResultClass(),
                        JsonResultClass(),
                        ContentResultClass(),
                        RedirectResultClass(),
                        RedirectToActionResultClass(),
                        RedirectToRouteResultClass())
                    .AddMembers(generatedControllers.ToArray<MemberDeclarationSyntax>());
            CompleteAndWriteFile(r4mvcNode, Path.Combine(projectRoot, R4MvcGeneratorService.R4MvcFileName));
        }

        public IEnumerable<ClassDeclarationSyntax> CreateViewOnlyControllerClasses(IList<ControllerDefinition> controllers)
        {
            foreach (var controller in controllers.Where(c => c.Namespace == null).OrderBy(c => c.Area).ThenBy(c => c.Name))
            {
                var className = !string.IsNullOrEmpty(controller.Area)
                    ? $"{controller.Area}Area_{controller.Name}Controller"
                    : $"{controller.Name}Controller";
                var controllerClass = ClassDeclaration(className)
                    .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                    .WithViewsClass(controller.Name, controller.Area, controller.Views);
                controller.FullyQualifiedGeneratedName = $"{_settings.R4MvcNamespace}.{className}";

                yield return controllerClass;
            }
        }

        public IEnumerable<ClassDeclarationSyntax> CreateAreaClasses(ILookup<string, ControllerDefinition> areaControllers)
        {
            foreach (var area in areaControllers.Where(a => !string.IsNullOrEmpty(a.Key)).OrderBy(a => a.Key))
            {
                var areaClass = ClassDeclaration(area.Key + "AreaClass")
                    .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithStringField("Name", area.Key, false, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)
                    .AddMembers(area
                        .OrderBy(c => c.Namespace == null).ThenBy(c => c.Name)
                        .Select(c => SyntaxNodeHelpers.CreateFieldWithDefaultInitializer(
                            c.Name,
                            c.FullyQualifiedGeneratedName,
                            c.FullyQualifiedR4ClassName ?? c.FullyQualifiedGeneratedName,
                            SyntaxKind.PublicKeyword,
                            SyntaxKind.ReadOnlyKeyword))
                        .ToArray<MemberDeclarationSyntax>());
                yield return areaClass;
            }
        }

        public CompilationUnitSyntax NewCompilationUnit()
        {
            // Create the root node and add usings, header, pragma
            return CompilationUnit()
                    .WithUsings(
                        "System.CodeDom.Compiler",
                        "System.Diagnostics",
                        "System.Threading.Tasks",
                        "Microsoft.AspNetCore.Mvc",
                        "Microsoft.AspNetCore.Routing",
                        _settings.R4MvcNamespace)
                    .WithHeader(_headerText)
                    .WithPragmaCodes(false, pragmaCodes);
        }

        public void CompleteAndWriteFile(CompilationUnitSyntax contents, string filePath)
        {
            contents = contents
                .NormalizeWhitespace()
                // NOTE reenable pragma codes after normalizing whitespace or it doesn't place on newline
                .WithPragmaCodes(true, pragmaCodes);

            _filePersistService.WriteFile(contents, filePath);
        }

        private ClassDeclarationSyntax IActionResultDerivedClass(string className, string baseClassName, Action<ConstructorMethodBuilder> constructorParts = null)
        {
            var result = new ClassBuilder(className)                                    // internal partial class {className}
                .WithModifiers(SyntaxKind.InternalKeyword, SyntaxKind.PartialKeyword)
                .WithBaseTypes(baseClassName, "IR4MvcActionResult")                     // : {baseClassName}, IR4MvcActionResult
                .WithConstructor(c => c
                    .WithOther(constructorParts)
                    .WithModifiers(SyntaxKind.PublicKeyword)                        // public ctor(
                    .WithStringParameter("area")                                    //  string area,
                    .WithStringParameter("controller")                              //  string controller,
                    .WithStringParameter("action")                                  //  string action,
                    .WithStringParameter("protocol", defaultsToNull: true)          //  string protocol = null)
                    .WithBody(b => b                                                // this.InitMVCT4Result(area, controller, action, protocol);
                        .MethodCall("this", "InitMVCT4Result", "area", "controller", "action", "protocol")))
                .WithStringProperty("Controller")                                       // public string Controller { get; set; }
                .WithStringProperty("Action")                                           // public string Action { get; set; }
                .WithStringProperty("Protocol")                                         // public string Protocol { get; set; }
                .WithProperty("RouteValueDictionary", "RouteValueDictionary");          // public RouteValueDictionary RouteValueDictionary { get; set; }

            return result.Build();
        }

        public ClassDeclarationSyntax ActionResultClass()
            => IActionResultDerivedClass(Constants.ActionResultClass, "ActionResult");

        public ClassDeclarationSyntax JsonResultClass()
            => IActionResultDerivedClass(Constants.JsonResultClass, "JsonResult",
                c => c.WithBaseConstructorCall(p => p.Null));                           // ctor : base(null)

        public ClassDeclarationSyntax ContentResultClass()
            => IActionResultDerivedClass(Constants.ContentResultClass, "ContentResult");

        public ClassDeclarationSyntax RedirectResultClass()
            => IActionResultDerivedClass(Constants.RedirectResultClass, "RedirectResult",
                c => c.WithBaseConstructorCall(p => p.Space));                          // ctor : base(" ")

        public ClassDeclarationSyntax RedirectToActionResultClass()
            => IActionResultDerivedClass(Constants.RedirectToActionResultClass, "RedirectToActionResult",
                c => c.WithBaseConstructorCall(p => p.Space, p => p.Space, p => p.Space));  // ctor : base(" ", " ", " ")

        public ClassDeclarationSyntax RedirectToRouteResultClass()
            => IActionResultDerivedClass(Constants.RedirectToRouteResultClass, "RedirectToRouteResult",
                c => c.WithBaseConstructorCall(p => p.Null));                           // ctor : base(null)
    }
}
