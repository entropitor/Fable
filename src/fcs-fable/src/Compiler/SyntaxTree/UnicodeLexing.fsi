// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module internal FSharp.Compiler.UnicodeLexing

open System.IO
open FSharp.Compiler.Features
open FSharp.Compiler.Text
open Internal.Utilities.Text.Lexing

type Lexbuf = LexBuffer<LexBufferChar>

val StringAsLexbuf: reportLibraryOnlyFeatures: bool * langVersion: LanguageVersion * string -> Lexbuf

val FunctionAsLexbuf:
    reportLibraryOnlyFeatures: bool * langVersion: LanguageVersion * bufferFiller: (LexBufferChar[] * int * int -> int) -> Lexbuf

val SourceTextAsLexbuf:
    reportLibraryOnlyFeatures: bool * langVersion: LanguageVersion * sourceText: ISourceText -> Lexbuf

#if !FABLE_COMPILER

/// Will not dispose of the stream reader.
val StreamReaderAsLexbuf:
    reportLibraryOnlyFeatures: bool * langVersion: LanguageVersion * reader: StreamReader -> Lexbuf

#endif //!FABLE_COMPILER
