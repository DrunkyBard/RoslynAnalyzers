﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using AnalyzeMe.Design.Analyzers.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace AnalyzeMe.Design.Analyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RxSubscribeMethodCodeFixProvider)), Shared]
    public sealed class RxSubscribeMethodCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(RxSubscribeMethodAnalyzer.RxSubscribeMethodDiagnosticId);
        private SyntaxTrivia WhiteSpace => SyntaxFactory.Whitespace(" ");
        private string MethodBodyComment => @"/*TODO: handle this!*/";

        public override FixAllProvider GetFixAllProvider()
        {
            return WellKnownFixAllProviders.BatchFixer;
        }

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var subscribeMethodInvocation = root.FindNode(diagnosticSpan) as InvocationExpressionSyntax;

            if (subscribeMethodInvocation == null)
            {
                return;
            }

            var methodArguments = subscribeMethodInvocation.ArgumentList;

            var newInvocationArguments = methodArguments.Arguments.First().NameColon != null
                ? CreateNamedArgumentsFrom(methodArguments)
                : CreateSimpleArgumentsFrom(methodArguments);
            var updatedRoot = root.ReplaceNode(methodArguments, newInvocationArguments);
            var updatedDocument = context.Document.WithSyntaxRoot(updatedRoot);
            
            context.RegisterCodeFix(
                CodeAction.Create("Add onError parameter", c => Task.FromResult(updatedDocument), "AddSubscribeOnErrorParameter"),
                diagnostic);
        }

        private ArgumentListSyntax CreateNamedArgumentsFrom(ArgumentListSyntax oldArguments)
        {
            var lastArgument = oldArguments.Arguments.Last();
            var onErrorNameColon = SyntaxFactory.NameColon("onError");
            var onErrorArgument = CreateOnErrorLambdaArgument(onErrorNameColon);
            onErrorArgument = FormatOnErrorArgument(onErrorArgument, lastArgument);
            var lastComma = lastArgument.GetAssociatedComma();
            var hasEol = lastComma.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            var eolTrivia = hasEol
                ? SyntaxFactory.EndOfLine(Environment.NewLine)
                : SyntaxFactory.Whitespace(String.Empty);
            var onErrorComma = SyntaxFactory.Token(SyntaxKind.CommaToken)
                .WithTrailingTrivia(eolTrivia);
            var newArguments = oldArguments.Arguments.Add(onErrorArgument);
            var newCommas = oldArguments.Arguments.GetSeparators().ToList();
            newCommas.Add(onErrorComma);

            return oldArguments.WithArguments(SyntaxFactory.SeparatedList(newArguments, newCommas));
        }

        private ArgumentListSyntax CreateSimpleArgumentsFrom(ArgumentListSyntax oldArguments)
        {
            var onNextArgument = oldArguments.Arguments.First();
            var afterOnNextArguments = oldArguments.Arguments.Skip(1).ToArray();
            var firstAfterOnNextArg = afterOnNextArguments.FirstOrDefault();
            var onErrorArgument = FormatOnErrorArgument(CreateOnErrorLambdaArgument(), onNextArgument, firstAfterOnNextArg);
            var subscribeMethodArguments = new[]
            {
                onNextArgument,
                onErrorArgument
            }.Union(afterOnNextArguments);

            SyntaxToken afterOnNextCommaToken;

            if (firstAfterOnNextArg == null)
            {
                afterOnNextCommaToken = SyntaxFactory.Token(SyntaxKind.None);
            }
            else
            {
                afterOnNextCommaToken = firstAfterOnNextArg.GetAssociatedComma();
            }

            var trailingCommaTokenTrivia = afterOnNextCommaToken.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia)
                ? SyntaxFactory.EndOfLine(Environment.NewLine)
                : SyntaxFactory.Whitespace(String.Empty);
            var onErrorCommaToken = SyntaxFactory
                .Token(SyntaxKind.CommaToken)
                .WithTrailingTrivia(trailingCommaTokenTrivia);
            var otherArgumentCommaTokens = oldArguments
                .ChildTokens()
                .Where(t => t.IsKind(SyntaxKind.CommaToken))
                .Skip(1);
            var tList = new List<SyntaxToken>();
            tList.Add(onErrorCommaToken);

            if (!afterOnNextCommaToken.IsKind(SyntaxKind.None))
            {
                tList.Add(afterOnNextCommaToken);
            }

            tList.AddRange(otherArgumentCommaTokens);

            return oldArguments.WithArguments(SyntaxFactory.SeparatedList(subscribeMethodArguments, tList));
        }

        private ArgumentSyntax FormatOnErrorArgument(ArgumentSyntax onErrorArgument, ArgumentSyntax onNextArgument, ArgumentSyntax firstArgumentAfterOnNext = null)
        {
            SyntaxTrivia whitespace;

            if (firstArgumentAfterOnNext != null)
            {
                if (firstArgumentAfterOnNext.GetAssociatedComma().TrailingTrivia.Any(x => x.IsKind(SyntaxKind.EndOfLineTrivia)))
                {
                    whitespace = firstArgumentAfterOnNext.ExtractWhitespace();
                }
                else
                {
                    whitespace = WhiteSpace;
                }

                return onErrorArgument.WithLeadingTrivia(whitespace);
            }

            if (onNextArgument.HasLeadingTrivia)
            {
                whitespace = onNextArgument.ExtractWhitespace();

                return onErrorArgument.WithLeadingTrivia(whitespace);
            }

            return onErrorArgument.WithLeadingTrivia(WhiteSpace);
        }

        private ArgumentSyntax CreateOnErrorLambdaArgument(NameColonSyntax nameColon = null)
        {
            var onErrorNameColon = nameColon;
            var parameter = SyntaxFactory
                .Parameter(SyntaxFactory.Identifier("ex"));
                //.WithLeadingTrivia(WhiteSpace);
            var lambdaBodyToken = SyntaxFactory.Token
                (
                    SyntaxFactory.TriviaList(),
                    SyntaxKind.OpenBraceToken,
                    SyntaxFactory.TriviaList(WhiteSpace, SyntaxFactory.Comment(MethodBodyComment), WhiteSpace)
                );
            var lambdaBody = SyntaxFactory.Block(lambdaBodyToken, default(SyntaxList<StatementSyntax>), SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
            var lambdaExpression = SyntaxFactory.SimpleLambdaExpression(parameter, lambdaBody)
                .WithArrowToken(
                    SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken)
                        .WithLeadingTrivia(WhiteSpace)
                        .WithTrailingTrivia(WhiteSpace));
            var nopToken = SyntaxFactory.Token(SyntaxKind.None);
            var onErrorArgument = SyntaxFactory.Argument(onErrorNameColon, nopToken, lambdaExpression);
            
            return onErrorArgument;
        }
    }
}
