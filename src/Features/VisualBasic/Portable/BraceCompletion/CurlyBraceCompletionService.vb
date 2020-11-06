﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Composition
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.BraceCompletion
Imports Microsoft.CodeAnalysis.Host.Mef
Imports Microsoft.CodeAnalysis.VisualBasic

<Export(LanguageNames.VisualBasic, GetType(IBraceCompletionService)), [Shared]>
Friend Class CurlyBraceCompletionService
    Inherits AbstractBraceCompletionService

    <ImportingConstructor>
    <Obsolete(MefConstruction.ImportingConstructorMessage, True)>
    Public Sub New()
        MyBase.New()
    End Sub

    Protected Overrides ReadOnly Property OpeningBrace As Char
        Get
            Return CurlyBrace.OpenCharacter
        End Get
    End Property

    Protected Overrides ReadOnly Property ClosingBrace As Char
        Get
            Return CurlyBrace.CloseCharacter
        End Get
    End Property

    Public Overrides Async Function AllowOverTypeAsync(context As BraceCompletionContext, cancellationToken As CancellationToken) As Task(Of Boolean)
        Return Await CheckCurrentPositionAsync(context.Document, context.CaretLocation, cancellationToken).ConfigureAwait(False) _
            And Await CheckClosingTokenKindAsync(context.Document, context.ClosingPoint, cancellationToken).ConfigureAwait(False)
    End Function

    Public Overrides Async Function IsValidForBraceCompletionAsync(brace As Char, openingPosition As Integer, document As Document, cancellationToken As CancellationToken) As Task(Of Boolean)
        If OpeningBrace = brace And Await InterpolationBraceCompletionService.IsContextAsync(document, openingPosition, cancellationToken).ConfigureAwait(False) Then
            Return False
        End If

        Return Await MyBase.IsValidForBraceCompletionAsync(brace, openingPosition, document, cancellationToken).ConfigureAwait(False)
    End Function

    Protected Overrides Function IsValidOpeningBraceToken(token As SyntaxToken) As Boolean
        Return token.IsKind(SyntaxKind.OpenBraceToken)
    End Function

    Protected Overrides Function IsValidClosingBraceToken(token As SyntaxToken) As Boolean
        Return token.IsKind(SyntaxKind.CloseBraceToken)
    End Function
End Class
