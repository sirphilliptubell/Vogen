﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vogen.Diagnostics;

namespace Vogen;

internal static class BuildWorkItems
{
    public static VoWorkItem? TryBuild(VoTarget target,
        SourceProductionContext context, 
        VogenConfiguration? globalConfig)
    {
        TypeDeclarationSyntax voTypeSyntax = target.VoSyntaxInformation;

        INamedTypeSymbol voSymbolInformation = target.VoSymbolInformation;

        ImmutableArray<AttributeData> attributes = voSymbolInformation.GetAttributes();

        if (attributes.Length == 0)
        {
            return null;
        }

        AttributeData? voAttribute = attributes.SingleOrDefault(
            a => a.AttributeClass?.FullName() is "Vogen.ValueObjectAttribute");

        if (voAttribute is null)
        {
            return null;
        }

        foreach (var eachConstructor in voSymbolInformation.Constructors)
        {
            // no need to check for default constructor as it's already defined
            // and the user will see: error CS0111: Type 'Foo' already defines a member called 'Foo' with the same parameter type
            if (eachConstructor.Parameters.Length > 0)
            {
                context.ReportDiagnostic(DiagnosticItems.CannotHaveUserConstructors(eachConstructor));
            }
        }

        ImmutableArray<TypedConstant> args = voAttribute.ConstructorArguments;

        // build the configuration but log any diagnostics (we have a separate analyzer that does that)
        var localConfig = GlobalConfigFilter.BuildConfigurationFromAttribute(voAttribute, context);
        
        if (localConfig == null)
        {
            return null;
        }

        var config = VogenConfiguration.Combine(localConfig.Value, globalConfig);

        foreach (TypedConstant arg in args)
        {
            if (arg.Kind == TypedConstantKind.Error)
            {
                break;
            }
        }

        INamedTypeSymbol? containingType = target.ContainingType;// context.SemanticModel.GetDeclaredSymbol(context.Node)!.ContainingType;
        if (containingType != null)
        {
            context.ReportDiagnostic(DiagnosticItems.TypeCannotBeNested(voSymbolInformation, containingType));
        }

        var instanceProperties = TryBuildInstanceProperties(attributes, voSymbolInformation, context);

        MethodDeclarationSyntax? validateMethod = null;
        MethodDeclarationSyntax? normalizeInputMethod = null;

        // add any validator or normalize methods it finds
        foreach (var memberDeclarationSyntax in voTypeSyntax.Members)
        {
            if (memberDeclarationSyntax is MethodDeclarationSyntax mds)
            {
                string? methodName = mds.Identifier.Value?.ToString();

                if (StringComparer.OrdinalIgnoreCase.Compare(methodName, "validate") == 0)
                {
                    if (!IsMethodStatic(mds))
                    {
                        context.ReportDiagnostic(DiagnosticItems.ValidationMustBeStatic(mds));
                    }

                    TypeSyntax returnTypeSyntax = mds.ReturnType;

                    if (returnTypeSyntax.ToString() != "Validation")
                    {
                        context.ReportDiagnostic(DiagnosticItems.ValidationMustReturnValidationType(mds));
                    }

                    validateMethod = mds;
                }

                if (StringComparer.OrdinalIgnoreCase.Compare(methodName, "normalizeinput") == 0)
                {
                    if (!(IsMethodStatic(mds)))
                    {
                        context.ReportDiagnostic(DiagnosticItems.NormalizeInputMethodMustBeStatic(mds));
                    }

                    if (mds.ParameterList.Parameters.Count != 1)
                    {
                        context.ReportDiagnostic(DiagnosticItems.NormalizeInputMethodTakeOneParameterOfUnderlyingType(mds));
                        return null;
                    }

                    TypeSyntax? fistParamType = mds.ParameterList.Parameters[0].Type;
                    INamedTypeSymbol? fptSymbol = target.SemanticModel.GetSymbolInfo(fistParamType!).Symbol as INamedTypeSymbol;
                    bool sameType2 = SymbolEqualityComparer.Default.Equals(fptSymbol, config.UnderlyingType);
                    if (!sameType2)
                    {
                        context.ReportDiagnostic(DiagnosticItems.NormalizeInputMethodTakeOneParameterOfUnderlyingType(mds));
                        return null;
                    }

                    INamedTypeSymbol? returnTypeOfMethod = target.SemanticModel.GetSymbolInfo(mds.ReturnType).Symbol as INamedTypeSymbol;
                    
                    bool sameType = SymbolEqualityComparer.Default.Equals(returnTypeOfMethod, config.UnderlyingType);

                     if (!sameType)
                    {
                        context.ReportDiagnostic(DiagnosticItems.NormalizeInputMethodMustReturnUnderlyingType(mds));
                    }

                    normalizeInputMethod = mds;
                }
            }
        }

        if (SymbolEqualityComparer.Default.Equals(voSymbolInformation, config.UnderlyingType))
        {
            context.ReportDiagnostic(DiagnosticItems.UnderlyingTypeMustNotBeSameAsValueObjectType(voSymbolInformation));
        }

        if (config.UnderlyingType.ImplementsInterfaceOrBaseClass(typeof(ICollection)))
        {
            context.ReportDiagnostic(DiagnosticItems.UnderlyingTypeCannotBeCollection(voSymbolInformation, config.UnderlyingType!));
        }

        bool isValueType = true;
        if (config.UnderlyingType != null)
        {
            isValueType = config.UnderlyingType.IsValueType;
        }

        return new VoWorkItem
        {
            InstanceProperties = instanceProperties.ToList(),
            TypeToAugment = voTypeSyntax,
            IsValueType = isValueType,
            UnderlyingType = config.UnderlyingType,
            Conversions = config.Conversions,
            TypeForValidationExceptions = config.ValidationExceptionType,
            ValidateMethod = validateMethod,
            NormalizeInputMethod = normalizeInputMethod,
            FullNamespace = voSymbolInformation.FullNamespace()
        };
    }

    private static bool IsMethodStatic(MethodDeclarationSyntax mds) => mds.DescendantTokens().Any(t => t.IsKind(SyntaxKind.StaticKeyword));

    private static IEnumerable<InstanceProperties> TryBuildInstanceProperties(
        ImmutableArray<AttributeData> attributes,
        INamedTypeSymbol voClass,
        SourceProductionContext context)
    {
        var matchingAttributes =
            attributes.Where(a => a.AttributeClass?.ToString() is "Vogen.InstanceAttribute");

        foreach (AttributeData? eachAttribute in matchingAttributes)
        {
            if (eachAttribute == null)
            {
                continue;
            }

            ImmutableArray<TypedConstant> constructorArguments = eachAttribute.ConstructorArguments;

            if (constructorArguments.Length == 0)
            {
                continue;
            }

            var name = (string?) constructorArguments[0].Value;

            if (name is null)
            {
                context.ReportDiagnostic(DiagnosticItems.InstanceMethodCannotHaveNullArgumentName(voClass));
            }

            var value = constructorArguments[1].Value;

            if (value is null)
            {
                context.ReportDiagnostic(DiagnosticItems.InstanceMethodCannotHaveNullArgumentValue(voClass));
            }

            if (name is null || value is null)
            {
                continue;
            }

            yield return new InstanceProperties(name, value);
        }
    }
}