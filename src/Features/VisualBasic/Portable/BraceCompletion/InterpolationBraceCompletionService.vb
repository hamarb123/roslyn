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
Friend Class InterpolationBraceCompletionService
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

    Protected Overrides Function CheckOpeningPointAsync(token As SyntaxToken, position As Integer, document As Document, cancellationToken As CancellationToken) As Task(Of Boolean)
        Return Task.FromResult(IsValidOpeningBraceToken(token))
    End Function

    Public Overrides Function AllowOverTypeAsync(context As BraceCompletionContext, cancellationToken As CancellationToken) As Task(Of Boolean)
        Return CheckClosingTokenKindAsync(context.Document, context.ClosingPoint, cancellationToken)
    End Function

    Public Overrides Async Function IsValidForBraceCompletionAsync(brace As Char, openingPosition As Integer, document As Document, cancellationToken As CancellationToken) As Task(Of Boolean)
        Return OpeningBrace = brace And Await IsContextAsync(document, openingPosition, cancellationToken).ConfigureAwait(False)
    End Function

    Protected Overrides Function IsValidOpeningBraceToken(token As SyntaxToken) As Boolean
        Return token.IsKind(SyntaxKind.DollarSignDoubleQuoteToken, SyntaxKind.InterpolatedStringTextToken) OrElse
                   (token.IsKind(SyntaxKind.CloseBraceToken) AndAlso token.Parent.IsKind(SyntaxKind.Interpolation))
    End Function

    Protected Overrides Function IsValidClosingBraceToken(token As SyntaxToken) As Boolean
        Return token.IsKind(SyntaxKind.CloseBraceToken)
    End Function

    Public Shared Async Function IsContextAsync(document As Document, position As Integer, cancellationToken As CancellationToken) As Task(Of Boolean)
        If position = 0 Then
            Return False
        End If

        ' Check to see if the character to the left of the position is an open curly brace. Note that we have to
        ' count braces to ensure that the character isn't actually an escaped brace.
        Dim text = Await document.GetTextAsync(cancellationToken).ConfigureAwait(False)
        Dim index = position - 1
        Dim openCurlyCount = 0
        For index = index To 0 Step -1
            If text(index) = "{"c Then
                openCurlyCount += 1
            Else
                Exit For
            End If
        Next

        If openCurlyCount Mod 2 > 0 Then
            Return False
        End If

        ' Next, check to see if the token we're typing is part of an existing interpolated string.
        '
        Dim root = Await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(False)
        Dim token = root.FindTokenOnRightOfPosition(position)

        If Not token.Span.IntersectsWith(position) Then
            Return False
        End If

        Return token.IsKind(SyntaxKind.DollarSignDoubleQuoteToken, SyntaxKind.InterpolatedStringTextToken) OrElse
               (token.IsKind(SyntaxKind.CloseBraceToken) AndAlso token.Parent.IsKind(SyntaxKind.Interpolation))
    End Function
End Class
