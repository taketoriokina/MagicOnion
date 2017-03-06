﻿using MagicOnion.CodeAnalysis;
using MagicOnion.Generator;
using Microsoft.CodeAnalysis;
using Mono.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MagicOnion.CodeGenerator
{
    class CommandlineArguments
    {
        // moc.exe

        public string InputPath { get; private set; }
        public string OutputPath { get; private set; }
        public bool UnuseUnityAttr { get; private set; }
        public List<string> ConditionalSymbols { get; private set; }
        public string NamespaceRoot { get; private set; }

        public bool IsParsed { get; set; }

        public CommandlineArguments(string[] args)
        {
            ConditionalSymbols = new List<string>();
            NamespaceRoot = "MagicOnion";

            var option = new OptionSet()
            {
                { "i|input=", "[required]Input path of analyze csproj", x => { InputPath = x; } },
                { "o|output=", "[required]Output path(file) or directory base(in separated mode)", x => { OutputPath = x; } },
                { "u|unuseunityattr", "[optional, default=false]Unuse UnityEngine's RuntimeInitializeOnLoadMethodAttribute on MagicOnionInitializer", _ => { UnuseUnityAttr = true; } },
                { "c|conditionalsymbol=", "[optional, default=empty]conditional compiler symbol", x => { ConditionalSymbols.AddRange(x.Split(',')); } },
                { "n|namespace=", "[optional, default=MagicOnion]Set namespace root name", x => { NamespaceRoot = x; } },
            };
            if (args.Length == 0)
            {
                goto SHOW_HELP;
            }
            else
            {
                option.Parse(args);

                if (InputPath == null || OutputPath == null)
                {
                    Console.WriteLine("Invalid Argument:" + string.Join(" ", args));
                    Console.WriteLine();
                    goto SHOW_HELP;
                }

                IsParsed = true;
                return;
            }

            SHOW_HELP:
            Console.WriteLine("moc arguments help:");
            option.WriteOptionDescriptions(Console.Out);
            IsParsed = false;
        }
    }


    class Program
    {
        static void Main(string[] args)
        {
            var cmdArgs = new CommandlineArguments(args);
            if (!cmdArgs.IsParsed)
            {
                return;
            }

            // Generator Start...

            var sw = Stopwatch.StartNew();
            Console.WriteLine("Project Compilation Start:" + cmdArgs.InputPath);

            var collector = new MethodCollector(cmdArgs.InputPath, cmdArgs.ConditionalSymbols);

            Console.WriteLine("Project Compilation Complete:" + sw.Elapsed.ToString());
            Console.WriteLine();

            sw.Restart();
            Console.WriteLine("Method Collect Start");

            var definitions = collector.Visit();

            GenericSerializationInfo[] genericInfos;
            EnumSerializationInfo[] enumInfos;
            ExtractResolverInfo(definitions, out genericInfos, out enumInfos);

            Console.WriteLine("Method Collect Complete:" + sw.Elapsed.ToString());

            Console.WriteLine("Output Generation Start");
            sw.Restart();

            var enumTemplates = enumInfos.GroupBy(x => x.Namespace)
                .OrderBy(x => x.Key)
                .Select(x => new EnumTemplate()
                {
                    Namespace = x.Key,
                    enumSerializationInfos = x.ToArray()
                })
                .ToArray();

            var resolverTemplate = new ResolverTemplate()
            {
                Namespace = cmdArgs.NamespaceRoot + ".Resolvers",
                FormatterNamespace = cmdArgs.NamespaceRoot + ".Formatters",
                ResolverName = "MagicOnionResolver",
                registerInfos = genericInfos.OrderBy(x => x.FullName).Cast<IResolverRegisterInfo>().Concat(enumInfos.OrderBy(x => x.FullName)).ToArray()
            };

            var texts = definitions
                .GroupBy(x => x.Namespace)
                .OrderBy(x => x.Key)
                .Select(x => new CodeTemplate()
                {
                    Namespace = x.Key,
                    Interfaces = x.ToArray()
                })
                .ToArray();

            var registerTemplate = new RegisterTemplate
            {
                Namespace = cmdArgs.NamespaceRoot,
                Interfaces = definitions.Where(x => x.IsServiceDifinition).ToArray(),
                UnuseUnityAttribute = cmdArgs.UnuseUnityAttr
            };

            var sb = new StringBuilder();
            sb.AppendLine(registerTemplate.TransformText());
            sb.AppendLine(resolverTemplate.TransformText());
            foreach (var item in enumTemplates)
            {
                sb.AppendLine(item.TransformText());
            }

            foreach (var item in texts)
            {
                sb.AppendLine(item.TransformText());
            }
            Output(cmdArgs.OutputPath, sb.ToString());

            Console.WriteLine("String Generation Complete:" + sw.Elapsed.ToString());
            Console.WriteLine();
        }

        static void Output(string path, string text)
        {
            path = path.Replace("global::", "");

            const string prefix = "[Out]";
            Console.WriteLine(prefix + path);

            var fi = new FileInfo(path);
            if (!fi.Directory.Exists)
            {
                fi.Directory.Create();
            }

            System.IO.File.WriteAllText(path, text);
        }

        static readonly SymbolDisplayFormat binaryWriteFormat = new SymbolDisplayFormat(
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly);

        static readonly HashSet<string> embeddedTypes = new HashSet<string>(new string[]
        {
            "short",
            "int",
            "long",
            "ushort",
            "uint",
            "ulong",
            "float",
            "double",
            "bool",
            "byte",
            "sbyte",
            "decimal",
            "char",
            "string",
            "System.Guid",
            "System.TimeSpan",
            "System.DateTime",
            "System.DateTimeOffset",

            "MessagePack.Nil",

            // and arrays
            
            "short[]",
            "int[]",
            "long[]",
            "ushort[]",
            "uint[]",
            "ulong[]",
            "float[]",
            "double[]",
            "bool[]",
            "byte[]",
            "sbyte[]",
            "decimal[]",
            "char[]",
            "string[]",
            "System.DateTime[]",
            "System.ArraySegment<byte>",
            "System.ArraySegment<byte>?",

            // extensions

            "UnityEngine.Vector2",
            "UnityEngine.Vector3",
            "UnityEngine.Vector4",
            "UnityEngine.Quaternion",
            "UnityEngine.Color",
            "UnityEngine.Bounds",
            "UnityEngine.Rect",

            "System.Reactive.Unit",
        });


        static void ExtractResolverInfo(InterfaceDefintion[] definitions, out GenericSerializationInfo[] genericInfoResults, out EnumSerializationInfo[] enumInfoResults)
        {
            var genericInfos = new List<GenericSerializationInfo>();
            var enumInfos = new List<EnumSerializationInfo>();

            foreach (var method in definitions.SelectMany(x => x.Methods))
            {
                IArrayTypeSymbol array = null;
                INamedTypeSymbol enumType = null;
                bool isCheckedParamType = false;

                // return type
                if (method.UnwrappedOriginalResposneTypeSymbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Array)
                {
                    array = method.UnwrappedOriginalResposneTypeSymbol as IArrayTypeSymbol;
                    if (embeddedTypes.Contains(array.ToString())) continue;
                    goto MAKE_ARRAY;
                }
                else if (method.UnwrappedOriginalResposneTypeSymbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
                {
                    enumType = method.UnwrappedOriginalResposneTypeSymbol as INamedTypeSymbol;
                    goto MAKE_ENUM;
                }

                PARAM_TYPE:
                isCheckedParamType = true;

                // paramter type
                if (method.Parameters.Length == 1)
                {
                    var p = method.Parameters[0];
                    if (p.OriginalSymbol.Type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Array)
                    {
                        array = p.OriginalSymbol.Type as IArrayTypeSymbol;
                        if (embeddedTypes.Contains(array.ToString())) continue;
                        goto MAKE_ARRAY;
                    }
                    else if (p.OriginalSymbol.Type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
                    {
                        enumType = p.OriginalSymbol.Type as INamedTypeSymbol;
                        goto MAKE_ENUM;
                    }

                }
                else if (method.Parameters.Length != 0)
                {
                    // create dynamicargumenttuple
                    var parameterArguments = method.Parameters.Select(x => x.OriginalSymbol)
                       .Select(x => $"default({x.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})")
                       .ToArray();

                    var typeArguments = method.Parameters.Select(x => x.OriginalSymbol).Select(x => x.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

                    var tupleInfo = new GenericSerializationInfo
                    {
                        FormatterName = $"global::MagicOnion.DynamicArgumentTupleFormatter<{string.Join(", ", typeArguments)}>({string.Join(", ", parameterArguments)})",
                        FullName = $"global::MagicOnion.DynamicArgumentTuple<{string.Join(", ", typeArguments)}>",
                    };
                    genericInfos.Add(tupleInfo);
                }

                continue;

                MAKE_ARRAY:
                var arrayInfo = new GenericSerializationInfo
                {
                    FormatterName = $"global::MessagePack.Formatters.ArrayFormatter<{array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()",
                    FullName = array.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                };
                genericInfos.Add(arrayInfo);

                if (!isCheckedParamType)
                {
                    goto PARAM_TYPE;
                }

                continue;

                MAKE_ENUM:
                var enumInfo = new EnumSerializationInfo
                {
                    Name = enumType.Name,
                    Namespace = enumType.ContainingNamespace.IsGlobalNamespace ? null : enumType.ContainingNamespace.ToDisplayString(),
                    FullName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    UnderlyingType = enumType.EnumUnderlyingType.ToDisplayString(binaryWriteFormat)
                };
                enumInfos.Add(enumInfo);

                if (!isCheckedParamType)
                {
                    goto PARAM_TYPE;
                }

                continue;
            }

            genericInfoResults = genericInfos.Distinct().ToArray();
            enumInfoResults = enumInfos.Distinct().ToArray();
        }
    }
}