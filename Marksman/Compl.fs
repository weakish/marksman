﻿module Marksman.Compl

open System

open System.IO
open Ionide.LanguageServerProtocol.Logging
open Ionide.LanguageServerProtocol.Types

open FSharpPlus.GenericBuilders

open Marksman.Config
open Marksman.Cst
open Marksman.Index
open Marksman.Refs
open Marksman.Workspace
open Marksman.Misc
open Marksman.Text

let private logger = LogProvider.getLoggerByName "Compl"

[<RequireQualifiedAccess>]
type PartialElement =
    | WikiLink of dest: option<TextNode> * heading: option<TextNode> * range: Range
    | InlineLink of
        text: option<TextNode> *
        path: option<TextNode> *
        anchor: option<TextNode> *
        range: Range
    | ReferenceLink of label: option<TextNode> * range: Range
    // TODO: consider moving tag opening out of PartialElement due to
    // complications in findCompletableAtPos
    | TagOpening of cursorPos: Position

    override this.ToString() =
        match this with
        | PartialElement.WikiLink (dest, heading, range) ->
            $"WL {range}: dest={Node.fmtOptText dest}; heading={Node.fmtOptText heading}"
        | PartialElement.InlineLink (text, path, anchor, range) ->
            $"IL {range}: text={Node.fmtOptText text}; path={Node.fmtOptText path}; anchor={Node.fmtOptText anchor}"
        | PartialElement.ReferenceLink (label, range) ->
            $"RL {range}: label={Node.fmtOptText label}"
        | TagOpening pos -> $"TO: cursorPos={pos}"

module PartialElement =
    open FSharpPlus.Operators

    let range =
        function
        | PartialElement.WikiLink (_, _, range)
        | PartialElement.InlineLink (_, _, _, range)
        | PartialElement.ReferenceLink (_, range) -> range
        | PartialElement.TagOpening cursorPos -> { Start = cursorPos; End = cursorPos } // empty range

    let linkInLine (line: Line) (pos: Position) : option<PartialElement> =
        monad' {
            let! startCursor =
                match Line.toCursorAt pos line with
                | Some cursor -> Cursor.backward cursor
                | _ when Line.endsAt pos line -> Line.endCursor line
                | _ -> None

            let findOpenOrWs =
                Cursor.tryFindCharMatching Cursor.backward (fun c ->
                    Char.IsWhiteSpace(c) || c = '[' || c = '(')

            let! precedingPunct = findOpenOrWs startCursor
            let lineRange = Line.range line

            match Cursor.backwardChar precedingPunct, Cursor.char precedingPunct with
            | Some '[', '[' -> // wiki
                let elementStart = Cursor.backward precedingPunct |> Option.get

                let findEnd =
                    Cursor.tryFindCharMatching Cursor.forward (fun c ->
                        Char.IsWhiteSpace(c) || c = ']' || c = '[' || c = ')' || c = '(')

                // No need to try to match ]] because then it would be a proper Element, not
                // just a partial one
                let inputEnd = Cursor.forward precedingPunct >>= findEnd

                let elementEnd =
                    match inputEnd with
                    | Some endCursor when (Cursor.char endCursor) = ']' -> Cursor.forward endCursor
                    | other -> other

                let elementRangeEnd =
                    elementEnd |>> Cursor.pos |> Option.defaultValue lineRange.End

                let elementRange = Range.Mk(Cursor.pos elementStart, elementRangeEnd)

                let dest, heading =
                    monad' {
                        let! inputStart =
                            (Cursor.forward precedingPunct)
                            |> Option.filter (fun x -> Cursor.pos x < elementRangeEnd)

                        let inputRangeStart = Cursor.pos inputStart

                        let inputRangeEnd =
                            inputEnd |>> Cursor.pos |> Option.defaultValue lineRange.End

                        let inputRange = Range.Mk(inputRangeStart, inputRangeEnd)
                        let inputNode = Node.mkText (line.text.Substring(inputRange)) inputRange
                        let url = Url.ofUrlNode inputNode
                        url.url, url.anchor
                    }
                    |> Option.defaultValue (None, None)


                PartialElement.WikiLink(dest, heading, elementRange)
            | _, '[' ->
                let elementStart = precedingPunct
                let inputStart = Cursor.forward precedingPunct

                let findEnd =
                    Cursor.tryFindCharMatching Cursor.forward (fun c ->
                        Char.IsWhiteSpace(c) || c = ']')

                let inputEnd = inputStart >>= findEnd

                let elementEnd =
                    match inputEnd with
                    | Some endCursor when (Cursor.char endCursor) = ']' -> Cursor.forward endCursor
                    | other -> other

                let elementRangeEnd =
                    elementEnd |>> Cursor.pos |> Option.defaultValue lineRange.End

                let inputRange =
                    Range.Mk(
                        inputStart |>> Cursor.pos |> Option.defaultValue lineRange.End,
                        inputEnd |>> Cursor.pos |> Option.defaultValue lineRange.End
                    )

                let input =
                    if inputRange.IsEmpty() then
                        None
                    else
                        inputStart |> Option.map (fun _ -> line.text.Substring(inputRange))

                let inputNode = input |>> fun input -> Node.mkText input inputRange

                let elementRange = Range.Mk(Cursor.pos elementStart, elementRangeEnd)

                PartialElement.ReferenceLink(inputNode, elementRange)
            | _, '(' -> // "inline link"
                let findStart =
                    Cursor.tryFindCharMatching Cursor.backward (fun c ->
                        Char.IsWhiteSpace(c) || c = '[' || c = ']' || c = '(' || c = ')')

                let precedingClosingBracket =
                    Cursor.backward precedingPunct
                    |> Option.filter (fun c -> Cursor.char c = ']')

                let potentialStart =
                    precedingClosingBracket >>= Cursor.backward >>= findStart

                let elementStart, textNode =
                    match potentialStart with
                    | Some c when Cursor.char c = '[' ->
                        // proper inline link start [...](
                        let textStart = Cursor.forward c
                        let textEnd = Cursor.backward precedingPunct

                        let textNode =
                            Option.map2
                                (fun s e ->
                                    let range = Range.Mk(Cursor.pos s, Cursor.pos e)
                                    let text = line.text.Substring(range)
                                    Node.mkText text range)
                                textStart
                                textEnd

                        c, textNode
                    | _ -> precedingPunct, None

                let findEnd =
                    Cursor.tryFindCharMatching Cursor.forward (fun c ->
                        Char.IsWhiteSpace(c) || c = '[' || c = ']' || c = '(' || c = ')')

                let labelStart = Cursor.forward precedingPunct
                let labelEnd = labelStart >>= findEnd

                let labelRangeStart =
                    labelStart |>> Cursor.pos |> Option.defaultValue lineRange.End

                let labelRangeEnd =
                    labelEnd |>> Cursor.pos |> Option.defaultValue lineRange.End

                let labelRange = Range.Mk(labelRangeStart, labelRangeEnd)

                let labelNode =
                    if labelRange.IsEmpty() then
                        None
                    else
                        let text = line.text.Substring(labelRange)
                        Node.mkText text labelRange |> Some

                let path, anchor =
                    labelNode |>> Url.ofUrlNode |>> (fun url -> url.url, url.anchor)
                    |> Option.defaultValue (None, None)

                let elementEnd =
                    match labelEnd with
                    | Some endCursor when (Cursor.char endCursor) = ')' -> Cursor.forward endCursor
                    | other -> other

                let elementRangeEnd =
                    elementEnd |>> Cursor.pos |> Option.defaultValue lineRange.End

                let elementRange = Range.Mk(Cursor.pos elementStart, elementRangeEnd)

                PartialElement.InlineLink(textNode, path, anchor, elementRange)
            | _, _ -> return! None
        }

    let tagOpeningInLine (line: Line) (pos: Position) : option<PartialElement> =
        let potentialHash =
            (Line.toCursorAt pos line >>= Cursor.backward)
            // The cursor can be at EOF, so check the end of line
            |> Option.orElseWith (fun () -> Line.endCursor line)
            |> Option.map Cursor.char

        match potentialHash with
        | Some '#' -> Some(PartialElement.TagOpening pos)
        | _ -> None

    let inLine (line: Line) (pos: Position) : option<PartialElement> =
        let link = linkInLine line pos
        let tag () = tagOpeningInLine line pos

        link |> Option.orElseWith tag

    let inText (text: Text) (pos: Position) : option<PartialElement> =
        Line.ofPos text pos |> Option.bind (fun l -> inLine l pos)

type Completable =
    | E of Element
    | PE of PartialElement

module Completable =
    let isPartial =
        function
        | E _ -> false
        | PE _ -> true

type Prompt =
    | WikiDoc of input: string
    | WikiHeadingInSrcDoc of input: string
    | WikiHeadingInOtherDoc of destPart: string * headingPart: string
    | Reference of input: string
    | InlineDoc of input: string
    | InlineAnchorInSrcDoc of input: string
    | InlineAnchorInOtherDoc of pathPart: string * anchorPart: string
    | Tag of input: string

module Prompt =
    let ofCompletable (pos: Position) (compl: Completable) : option<Prompt> =
        let elementRange =
            match compl with
            | E el -> Element.range el
            | PE pel -> PartialElement.range pel

        if not (elementRange.ContainsInclusive(pos)) then
            None
        else
            match compl with
            // No completion
            | E (H _)
            | E (MLD _)
            | E (YML _) -> None
            // Wiki link
            | E (WL { data = { doc = doc; heading = None } }) ->
                Some(WikiDoc(Node.textOpt doc String.Empty))
            | E (WL { data = { doc = None; heading = Some heading } }) ->
                Some(WikiHeadingInSrcDoc heading.text)
            | E (WL { data = { doc = Some doc; heading = Some heading } }) ->
                if doc.range.ContainsInclusive(pos) then
                    Some(WikiDoc doc.text)
                else
                    Some(WikiHeadingInOtherDoc(doc.text, heading.text))
            // Markdown link
            | E (ML { data = MdLink.RF (_, label) })
            | E (ML { data = MdLink.RC label })
            | E (ML { data = MdLink.RS label }) -> Some(Reference label.text)
            | E (ML { data = MdLink.IL (_, None, _); range = _range }) ->
                Some(InlineDoc String.Empty)
            | E (ML { data = MdLink.IL (_, Some url, _) }) ->
                match Url.ofUrlNode url with
                | { url = path; anchor = None } -> Some(InlineDoc(Node.textOpt path String.Empty))
                | { url = None; anchor = Some anchor } -> Some(InlineAnchorInSrcDoc anchor.text)
                | { url = Some path; anchor = Some anchor } ->
                    if path.range.ContainsInclusive(pos) then
                        Some(InlineDoc path.text)
                    else
                        Some(InlineAnchorInOtherDoc(path.text, anchor.text))
            // Partial wiki link
            | PE (PartialElement.WikiLink (doc, None, _)) ->
                Some(WikiDoc(Node.textOpt doc String.Empty))
            | PE (PartialElement.WikiLink (None, heading, _)) ->
                Some(WikiHeadingInSrcDoc(Node.textOpt heading String.Empty))
            | PE (PartialElement.WikiLink (Some dest, Some heading, _)) ->
                Some(WikiHeadingInOtherDoc(dest.text, heading.text))
            // Partial markdown link
            | PE (PartialElement.InlineLink (_, path, None, _)) ->
                Some(InlineDoc(Node.textOpt path String.Empty))
            | PE (PartialElement.InlineLink (_, None, anchor, _)) ->
                Some(InlineAnchorInSrcDoc(Node.textOpt anchor String.Empty))
            | PE (PartialElement.InlineLink (_, Some path, Some anchor, _)) ->
                Some(InlineAnchorInOtherDoc(path.text, anchor.text))
            | PE (PartialElement.ReferenceLink (label, _)) ->
                Some(Reference(Node.textOpt label String.Empty))
            // Tags
            | E (T { data = { name = name } }) -> Some(Tag name.text)
            | PE (PartialElement.TagOpening _) -> Some(Tag String.Empty)

module CompletionHelpers =
    let wikiTargetLink (style: ComplWikiStyle) (doc: Doc) =
        let docPath = Doc.pathFromRoot doc

        match style with
        | TitleSlug -> Slug.str (Doc.name doc)
        | FileStem ->
            let name = docPath |> Path.GetFileNameWithoutExtension
            name.EncodeForWiki()
        | FilePathStem ->
            let extension = docPath |> Path.GetExtension
            let name = docPath.TrimSuffix(extension)
            name.EncodePathForWiki()

module Completions =
    let wikiDoc
        (style: ComplWikiStyle)
        (pos: Position)
        (compl: Completable)
        (doc: Doc)
        : option<CompletionItem> =
        let targetName = (Doc.name doc)
        let targetLink = CompletionHelpers.wikiTargetLink style doc

        match compl with
        | E (WL { data = { doc = input; heading = heading }; range = range })
        | PE (PartialElement.WikiLink (input, heading, range)) ->
            let inputRange =
                input
                |> Option.map Node.range
                |> Option.defaultValue (Range.Mk(pos, pos))

            match heading with
            | None ->
                let newText =
                    WikiLink.render (Some targetLink) None (Completable.isPartial compl)

                let filterText =
                    WikiLink.render (Some targetName) None (Completable.isPartial compl)

                let range = if Completable.isPartial compl then range else inputRange
                let textEdit = { Range = range; NewText = newText }

                Some
                    { CompletionItem.Create(targetName) with
                        Detail = Some(Doc.pathFromRoot doc)
                        TextEdit = Some textEdit
                        FilterText = Some filterText }
            | Some _ ->
                let newText = targetLink
                let range = inputRange
                let textEdit = { Range = range; NewText = newText }

                Some
                    { CompletionItem.Create(targetName) with
                        Detail = Some(Doc.pathFromRoot doc)
                        TextEdit = Some textEdit
                        FilterText = Some targetName }
        | _ -> None

    let wikiHeadingInSrcDoc
        (_style: ComplWikiStyle)
        (_pos: Position)
        (compl: Completable)
        (completionHeading: string)
        : option<CompletionItem> =
        match compl with
        | E (WL { data = { doc = None; heading = Some input }; range = range })
        | PE (PartialElement.WikiLink (None, Some input, range)) ->
            let newText =
                WikiLink.render
                    None
                    (completionHeading.EncodeForWiki() |> Some)
                    (Completable.isPartial compl)

            let range = if Completable.isPartial compl then range else input.range
            let textEdit = { Range = range; NewText = newText }

            Some
                { CompletionItem.Create(completionHeading) with
                    TextEdit = Some textEdit
                    FilterText = Some newText }
        | _ -> None

    let wikiHeadingInOtherDoc
        (style: ComplWikiStyle)
        (_pos: Position)
        (compl: Completable)
        (doc: Doc, heading: string)
        : option<CompletionItem> =
        let label = $"{Doc.name doc} / {heading}"

        match compl with
        | E (WL { data = { doc = Some destPart; heading = Some headingPart }
                  range = range })
        | PE (PartialElement.WikiLink (Some destPart, Some headingPart, range)) ->
            let targetLink = CompletionHelpers.wikiTargetLink style doc

            let newText =
                WikiLink.render
                    (targetLink |> Some)
                    (heading.EncodeForWiki() |> Some)
                    (Completable.isPartial compl)


            let filterText =
                WikiLink.render (Some targetLink) (Some heading) (Completable.isPartial compl)

            let range =
                if Completable.isPartial compl then
                    range
                else
                    Range.Mk(destPart.range.Start, headingPart.range.End)

            let textEdit = { Range = range; NewText = newText }

            Some
                { CompletionItem.Create(label) with
                    Detail = Some(Doc.pathFromRoot doc)
                    TextEdit = Some textEdit
                    FilterText = Some filterText }
        | _ -> None

    let reference (pos: Position) (compl: Completable) (def: MdLinkDef) : option<CompletionItem> =
        let data =
            match compl with
            | E (ML { data = MdLink.RF (_, label); range = range })
            | E (ML { data = MdLink.RC label; range = range })
            | E (ML { data = MdLink.RS label; range = range }) -> Some(Some label, range)
            | PE (PartialElement.ReferenceLink (label, range)) -> Some(label, range)
            | _ -> None

        match data with
        | None -> None
        | Some (label, range) ->
            let labelRange =
                label
                |> Option.map Node.range
                |> Option.defaultValue (Range.Mk(pos, pos))

            let range = if Completable.isPartial compl then range else labelRange
            let linkDefLabel = MdLinkDef.labelContent def

            let newText =
                if Completable.isPartial compl then
                    $"[{linkDefLabel}]"
                else
                    linkDefLabel

            let textEdit = { Range = range; NewText = newText }

            Some
                { CompletionItem.Create(linkDefLabel) with
                    Detail = MdLinkDef.titleContent def
                    Documentation = MdLinkDef.urlContent def |> Documentation.String |> Some
                    TextEdit = Some textEdit
                    FilterText = Some newText }

    let inlineDoc (pos: Position) (compl: Completable) (doc: Doc) : option<CompletionItem> =
        let targetPath = (Doc.pathFromRoot doc)
        let targetPathEncoded = targetPath.AbsPathUrlEncode()

        let detail =
            Some(Doc.name doc) |> Option.filter (fun x -> x <> targetPath)

        match compl with
        | E (ML { data = MdLink.IL (_, None, _) }) ->
            Some
                { CompletionItem.Create(targetPath) with
                    Detail = detail
                    TextEdit = Some { Range = Range.Mk(pos, pos); NewText = targetPathEncoded } }
        | E (ML { data = MdLink.IL (_, Some url, _) }) ->
            match Url.ofUrlNode url with
            | { url = Some url } ->
                Some
                    { CompletionItem.Create(targetPath) with
                        Detail = detail
                        TextEdit = Some { Range = url.range; NewText = targetPathEncoded } }
            | _ -> None
        | PE (PartialElement.InlineLink (Some _text, path, Some _anchor, _range)) ->
            let range =
                path
                |> Option.map Node.range
                |> Option.defaultValue (Range.Mk(pos, pos))

            Some
                { CompletionItem.Create(targetPath) with
                    Detail = detail
                    TextEdit = Some { Range = range; NewText = targetPathEncoded } }
        | PE (PartialElement.InlineLink (Some text, _path, None, range)) ->
            let newText =
                MdLink.renderInline (Node.text text |> Some) (Some targetPathEncoded) None

            Some
                { CompletionItem.Create(targetPath) with
                    Detail = detail
                    TextEdit = Some { Range = range; NewText = newText }
                    FilterText = Some newText }
        | _ -> None

    let inlineAnchorInSrcDoc
        (_pos: Position)
        (compl: Completable)
        (completionHeading: string)
        : option<CompletionItem> =
        let headingSlug = Slug.str completionHeading

        match compl with
        | E (ML { data = MdLink.IL (_, Some url, _) }) ->
            let url = Url.ofUrlNode url

            match url with
            | { url = None; anchor = Some anchor } ->
                let newText = headingSlug

                Some
                    { CompletionItem.Create(completionHeading) with
                        TextEdit = Some { Range = anchor.range; NewText = newText }
                        FilterText = Some newText }
            | _ -> None
        | PE (PartialElement.InlineLink (Some text, None, Some _anchor, range)) ->
            let newText = $"[{text.text}](#{headingSlug})"

            Some
                { CompletionItem.Create(completionHeading) with
                    TextEdit = Some { Range = range; NewText = newText }
                    FilterText = Some newText }
        | _ -> None

    let inlineAnchorInOtherDoc
        (_pos: Position)
        (compl: Completable)
        (targetDoc: Doc, targetHeading: string)
        : option<CompletionItem> =
        let targetPath = Doc.pathFromRoot targetDoc
        let targetPathEncoded = targetPath.AbsPathUrlEncode()
        let label = $"{targetPath} / {targetHeading}"

        let detail =
            Some(Doc.name targetDoc) |> Option.filter (fun x -> x <> targetPath)

        match compl with
        | E (ML { data = MdLink.IL (_, Some url, _) }) ->
            let url = Url.ofUrlNode url

            match url.url, url.anchor with
            | Some url, Some anchor ->
                let newText = $"{targetPathEncoded}#{Slug.str targetHeading}"
                let newRange = Range.Mk(url.range.Start, anchor.range.End)
                let filterText = $"{targetPathEncoded}#{targetHeading}"

                Some
                    { CompletionItem.Create(label) with
                        Detail = detail
                        TextEdit = Some { Range = newRange; NewText = newText }
                        FilterText = Some filterText }
            | _, _ -> None
        | PE (PartialElement.InlineLink (Some text, Some _path, Some _anchor, range)) ->
            let newText =
                $"[{text.text}]({targetPathEncoded}#{Slug.str targetHeading})"

            let filterText = $"[{text.text}]({targetPathEncoded}#{targetHeading})"

            Some
                { CompletionItem.Create(label) with
                    Detail = detail
                    TextEdit = Some { Range = range; NewText = newText }
                    FilterText = Some filterText }
        | _ -> None

    let tag
        (_pos: Position)
        (compl: Completable)
        (input: string)
        (tagName: string, numUsages: int)
        : option<CompletionItem> =
        let range =
            match compl with
            | E (T { data = { name = name } }) -> Some(Node.range name)
            | PE (PartialElement.TagOpening _ as peTag) -> Some(PartialElement.range peTag)
            | _ -> None

        match range with
        | None -> None
        | Some range ->
            let label = tagName
            let detail = $"{numUsages} usages"

            // IDEA: since we have numUsages we could provide sort text that would sort based on usages.
            Some
                { CompletionItem.Create(label) with
                    Detail = Some detail
                    // Use input as filter text to avoid any extra filtering on the editor's side
                    FilterText = Some input
                    TextEdit = Some { Range = range; NewText = label } }

module Candidates =
    let findDocCandidates
        (folder: Folder)
        (srcDoc: Doc)
        (destPart: option<InternName>)
        : array<Doc> =
        let candidates =
            match destPart with
            | None -> Folder.docs folder
            | Some name -> FileLink.filterFuzzyMatchingDocs folder srcDoc name

        candidates |> Seq.filter (fun d -> d <> srcDoc) |> Array.ofSeq

    let findHeadingCandidates
        (folder: Folder)
        (srcDoc: Doc)
        (destPart: option<InternName>)
        (headingPart: string)
        : array<Doc * string> =
        let targetDocs =
            destPart
            |> Option.map (FileLink.filterFuzzyMatchingDocs folder srcDoc)
            |> Option.defaultValue [ srcDoc ]

        let targetDocs =
            if destPart.IsSome then
                targetDocs |> Seq.filter (fun d -> d <> srcDoc)
            else
                targetDocs

        let inputSlug = Slug.ofString headingPart

        let matchingHeadings destDoc =
            Doc.index >> Index.headings <| destDoc
            // We are not interested in completing titles as headings
            |> Seq.filter (fun { data = h } -> Heading.isTitle h |> not)
            |> Seq.map (fun { data = h } -> Heading.name h)
            |> Seq.filter (fun h -> Slug.isSubSequence inputSlug (Slug.ofString h))
            // There may be several headings with the same name.
            // Remove duplicates in completion candidates
            |> Set.ofSeq

        let prepareForDoc d =
            let headings = matchingHeadings d
            let docWithHeadings = headings |> Seq.map (fun h -> d, h)
            docWithHeadings

        targetDocs |> Seq.collect prepareForDoc |> Array.ofSeq

    let findLinkDefCandidates (_folder: Folder) (srcDoc: Doc) (input: string) : array<MdLinkDef> =
        Index.filterLinkDefs
            (LinkLabel.isSubSequenceOf (LinkLabel.ofString input))
            (Doc.index srcDoc)
        |> Seq.map Node.data
        |> Array.ofSeq

    let findTagCandidates (folder: Folder) (_srcDoc: Doc) (input: string) : array<string * int> =
        let matchingTags =
            seq {
                for doc in Folder.docs folder do
                    for tag in Index.tags (Doc.index doc) do
                        let tagName = tag.data.name.text

                        if
                            input.ToLowerInvariant().IsSubSequenceOf(tagName.ToLowerInvariant())
                            && not (input.Equals(tagName))
                        then
                            yield tagName
            }
            |> Seq.countBy id

        matchingTags |> Array.ofSeq

let findCompletableAtPos (doc: Doc) (pos: Position) : option<Completable> =
    let link () = Doc.index doc |> Index.linkAtPos pos |> Option.map E

    let tag () =
        Doc.index doc
        |> Index.tags
        // Inclusive because we want to cover cases when the cursor is right after the tag's end
        |> Array.tryFind (fun { data = { name = name } } -> (Node.range name).ContainsInclusive(pos))
        |> Option.map (T >> E)

    let partialElement () = PartialElement.inText (Doc.text doc) pos |> Option.map PE

    // The priority is generally link > partialElement > tag. However, when partial
    // element is a tag opening, try to check for proper tag first.
    // In particular, this means that [[#f will be completed as a wiki link,
    // rather than a tag.
    match link () with
    | Some _ as link -> link
    | _ ->
        match partialElement () with
        | Some (PE (PartialElement.TagOpening _)) as tagOpening ->
            tag () |> Option.orElse tagOpening
        | Some _ as partialElement -> partialElement
        | None -> tag ()


let findCandidatesForCompl
    (folder: Folder)
    (srcDoc: Doc)
    (pos: Position)
    (compl: Completable)
    : array<CompletionItem> =
    let config = Folder.configOrDefault folder

    match Prompt.ofCompletable pos compl with
    | None -> [||]
    | Some (WikiDoc input) ->
        let destPart = Some(InternName input)
        let cand = Candidates.findDocCandidates folder srcDoc destPart

        cand
        |> Array.choose (Completions.wikiDoc (config.ComplWikiStyle()) pos compl)
    | Some (WikiHeadingInSrcDoc input) ->
        let cand = Candidates.findHeadingCandidates folder srcDoc None input

        cand
        |> Array.map snd
        |> Array.choose (Completions.wikiHeadingInSrcDoc (config.ComplWikiStyle()) pos compl)
    | Some (WikiHeadingInOtherDoc (destPart, headingPart)) ->
        let destPart = Some(InternName destPart)

        let cand =
            Candidates.findHeadingCandidates folder srcDoc destPart headingPart

        cand
        |> Array.choose (Completions.wikiHeadingInOtherDoc (config.ComplWikiStyle()) pos compl)
    | Some (Reference input) ->
        let cand = Candidates.findLinkDefCandidates folder srcDoc input
        cand |> Array.choose (Completions.reference pos compl)
    | Some (InlineDoc input) ->
        let cand =
            match InternName.ofUrl (config.CoreMarkdownFileExtensions()) input with
            | None when input.IsEmpty() -> Candidates.findDocCandidates folder srcDoc None
            | None -> [||]
            | Some destPart -> Candidates.findDocCandidates folder srcDoc (Some destPart)

        cand |> Array.choose (Completions.inlineDoc pos compl)
    | Some (InlineAnchorInSrcDoc input) ->
        let cand = Candidates.findHeadingCandidates folder srcDoc None input

        cand
        |> Array.map snd
        |> Array.choose (Completions.inlineAnchorInSrcDoc pos compl)
    | Some (InlineAnchorInOtherDoc (pathPart, anchorPart)) ->
        let cand =
            match InternName.ofUrl (config.CoreMarkdownFileExtensions()) pathPart with
            | None -> [||]
            | Some destPart ->
                Candidates.findHeadingCandidates folder srcDoc (Some destPart) anchorPart

        cand |> Array.choose (Completions.inlineAnchorInOtherDoc pos compl)
    | Some (Tag input) ->
        let cand = Candidates.findTagCandidates folder srcDoc input
        cand |> Array.choose (Completions.tag pos compl input)

let findCandidatesInDoc (folder: Folder) (doc: Doc) (pos: Position) : array<CompletionItem> =
    match findCompletableAtPos doc pos with
    | None ->
        logger.trace (Log.setMessage "No completion point found")
        [||]
    | Some compl ->
        logger.trace (Log.setMessage "Found completion point" >> Log.addContext "comp" compl)
        findCandidatesForCompl folder doc pos compl

let findCandidates (folder: Folder) (docUri: PathUri) (pos: Position) : array<CompletionItem> =
    let doc = Folder.tryFindDocByPath docUri folder

    match doc with
    | None -> [||]
    | Some doc -> findCandidatesInDoc folder doc pos
