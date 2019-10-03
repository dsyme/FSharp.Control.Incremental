namespace FSharp.Data.Adaptive

open FSharp.Data.Traceable

/// An adaptive reader for aset that allows to pull operations and exposes its current state.
type IHashSetReader<'T> = IOpReader<CountingHashSet<'T>, HashSetDelta<'T>>

/// Adaptive set datastructure.
type AdaptiveHashSet<'T> =
    /// Is the set constant?
    abstract member IsConstant : bool

    /// The current content of the set as aval.
    abstract member Content : aval<HashSet<'T>>

    /// Gets a new reader to the set.
    abstract member GetReader : unit -> IHashSetReader<'T>

and aset<'T> = AdaptiveHashSet<'T>

/// Internal implementations for aset operations.
module AdaptiveHashSetImplementation =

    let inline checkTag (value : 'a) (real : obj) = Unchecked.equals (value :> obj) real
    
    /// Core implementation for a dependent set.
    type AdaptiveHashSetImpl<'T>(createReader : unit -> IOpReader<HashSetDelta<'T>>) =
        let history = History(createReader, CountingHashSet.trace)
        let content = history |> AVal.map CountingHashSet.toHashSet

        /// Gets a new reader to the set.
        member x.GetReader() : IHashSetReader<'T> =
            history.NewReader()

        /// Current content of the set as aval.
        member x.Content =
            content

        interface AdaptiveHashSet<'T> with
            member x.IsConstant = false
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content

    /// Efficient implementation for an empty adaptive set.
    type EmptySet<'T> private() =   
        static let instance = EmptySet<'T>() :> aset<_>
        let content = AVal.constant HashSet.empty
        let reader = new History.Readers.EmptyReader<CountingHashSet<'T>, HashSetDelta<'T>>(CountingHashSet.trace) :> IHashSetReader<'T>
        static member Instance = instance
        
        member x.Content = content
        member x.GetReader() = reader
        
        interface AdaptiveHashSet<'T> with
            member x.IsConstant = true
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content

    /// Efficient implementation for a constant adaptive set.
    type ConstantSet<'T>(content : Lazy<HashSet<'T>>) =
        let value = AVal.delay (fun () -> content.Value)

        member x.Content = value

        member x.GetReader() =
            new History.Readers.ConstantReader<_,_>(
                CountingHashSet.trace,
                lazy (HashSet.addAll content.Value),
                lazy (CountingHashSet.ofHashSet content.Value)
            ) :> IHashSetReader<_>

        interface AdaptiveHashSet<'T> with
            member x.IsConstant = true
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content


    /// Reader for map operations.
    type MapReader<'A, 'B>(input : aset<'A>, mapping : 'A -> 'B) =
        inherit AbstractReader<HashSetDelta<'B>>(HashSetDelta.monoid)
            
        let cache = Cache mapping
        let reader = input.GetReader()
        
        override x.Compute(token) =
            reader.GetChanges token |> HashSetDelta.map (fun d ->
                match d with
                | Add(1, v) -> Add(cache.Invoke v)
                | Rem(1, v) -> Rem(cache.Revoke v)
                | _ -> unexpected()
            )
          
    /// Reader for choose operations.
    type ChooseReader<'A, 'B>(input : aset<'A>, mapping : 'A -> option<'B>) =
        inherit AbstractReader<HashSetDelta<'B>>(HashSetDelta.monoid)
            
        let cache = Cache mapping
        let r = input.GetReader()
        
        override x.Compute(token) =
            r.GetChanges token |> HashSetDelta.choose (fun d ->
                match d with
                | Add(1, v) -> 
                    match cache.Invoke v with
                    | Some v -> Some (Add v)
                    | None -> None

                | Rem(1, v) ->
                    match cache.Revoke v with
                    | Some v -> Some (Rem v)
                    | None -> None

                | _ -> 
                    unexpected()
            )

    /// Reader for filter operations.
    type FilterReader<'T>(input : aset<'T>, predicate : 'T -> bool) =
        inherit AbstractReader<HashSetDelta<'T>>(HashSetDelta.monoid)
            
        let cache = Cache predicate
        let r = input.GetReader()

        override x.Compute(token) =
            r.GetChanges token |> HashSetDelta.filter (fun d ->
                match d with
                | Add(1, v) -> cache.Invoke v
                | Rem(1, v) -> cache.Revoke v
                | _ -> unexpected()
            )

    /// Reader for fully dynamic uinon operations.
    type UnionReader<'T>(input : aset<aset<'T>>) =
        inherit AbstractDirtyReader<IHashSetReader<'T>, HashSetDelta<'T>>(HashSetDelta.monoid, checkTag "InnerReader")

        let reader = input.GetReader()
        let cache = 
            Cache(fun (inner : aset<'T>) -> 
                let r = inner.GetReader()
                r.Tag <- "InnerReader"
                r
            )
        
        override x.Compute(token, dirty) =
            let mutable deltas = 
                reader.GetChanges token |> HashSetDelta.collect (fun d ->
                    match d with
                    | Add(1, v) ->
                        // r is no longer dirty since we pull it here.
                        let r = cache.Invoke v
                        dirty.Remove r |> ignore
                        r.GetChanges token

                    | Rem(1, v) -> 
                        // r is no longer dirty since we either pull or destroy it here.
                        let deleted, r = cache.RevokeAndGetDeleted v
                        dirty.Remove r |> ignore
                        if deleted then 
                            // in case r was not dirty we need to explicitly remove ourselves
                            // from its output-set. if it was dirty it can't hurt to do so.
                            r.Outputs.Remove x |> ignore
                            CountingHashSet.removeAll r.State
                        else
                            r.GetChanges token
                                
                    | _ -> unexpected()
                )

            // finally pull all the dirty readers and accumulate the deltas.
            for d in dirty do
                deltas <- HashSetDelta.combine deltas (d.GetChanges token)

            deltas

    /// Reader for unioning a constant set of asets.
    type UnionConstantReader<'T>(input : HashSet<aset<'T>>) =
        inherit AbstractDirtyReader<IHashSetReader<'T>, HashSetDelta<'T>>(HashSetDelta.monoid, checkTag "InnerReader")

        let mutable isInitial = true
        let input = 
            input |> HashSet.map (fun s -> 
                let r = s.GetReader()
                r.Tag <- "InnerReader"
                r
            )
        
        override x.Compute(token, dirty) = 
            if isInitial then
                isInitial <- false
                // initially we need to pull all inner readers.
                (HashSetDelta.empty, input) ||> HashSet.fold (fun deltas r ->   
                    HashSetDelta.combine deltas (r.GetChanges token)
                )
            else
                // once evaluated only dirty readers need to be pulled.
                (HashSetDelta.empty, dirty) ||> Seq.fold (fun deltas r -> 
                    HashSetDelta.combine deltas (r.GetChanges token)
                )

    /// Reader for unioning a dynamic aset of immutable sets.
    type UnionHashSetReader<'T>(input : aset<HashSet<'T>>) =
        inherit AbstractReader<HashSetDelta<'T>>(HashSetDelta.monoid)

        let reader = input.GetReader()

        override x.Compute(token) =
            reader.GetChanges token |> HashSetDelta.collect (fun d ->
                match d with
                | Add(1, v) -> HashSet.addAll v
                | Rem(1, v) -> HashSet.removeAll v   
                | _ -> unexpected()
            )

    /// Reader for collect operations.
    type CollectReader<'A, 'B>(input : aset<'A>, mapping : 'A -> aset<'B>) =
        inherit AbstractDirtyReader<IHashSetReader<'B>, HashSetDelta<'B>>(HashSetDelta.monoid, checkTag "InnerReader")

        let reader = input.GetReader()
        let cache = 
            Cache(fun value -> 
                let reader = (mapping value).GetReader()
                reader.Tag <- "InnerReader"
                reader
            )

        override x.Compute(token,dirty) =
            let mutable deltas = 
                reader.GetChanges token |> HashSetDelta.collect (fun d ->
                    match d with
                    | Add(1, value) ->
                        // r is no longer dirty since we pull it here.
                        let r = cache.Invoke value
                        dirty.Remove r |> ignore
                        r.GetChanges token

                    | Rem(1, value) -> 
                        match cache.RevokeAndGetDeletedTotal value with
                        | Some (deleted, r) -> 
                            // r is no longer dirty since we either pull or destroy it here.
                            dirty.Remove r |> ignore
                            if deleted then 
                                // in case r was not dirty we need to explicitly remove ourselves
                                // from its output-set. if it was dirty it can't hurt to do so.
                                r.Outputs.Remove x |> ignore
                                CountingHashSet.removeAll r.State
                            else
                                r.GetChanges token
                        | None -> 
                            // weird
                            HashSetDelta.empty
                                
                    | _ -> unexpected()
                )
                
            // finally pull all the dirty readers and accumulate the deltas.
            for d in dirty do
                deltas <- HashSetDelta.combine deltas (d.GetChanges token)

            deltas

    /// Reader for aval<HashSet<_>>
    type AValReader<'S, 'A when 'S :> seq<'A>>(input : aval<'S>) =
        inherit AbstractReader<HashSetDelta<'A>>(HashSetDelta.monoid)

        let mutable oldSet = HashSet.empty

        override x.Compute(token) =
            let newSet = input.GetValue token :> seq<_> |> HashSet.ofSeq
            let deltas = HashSet.computeDelta oldSet newSet
            oldSet <- newSet
            deltas

    /// Reader for bind operations.
    type BindReader<'A, 'B>(input : aval<'A>, mapping : 'A -> aset<'B>) =
        inherit AbstractReader<HashSetDelta<'B>>(HashSetDelta.monoid)
            
        let mutable valChanged = 0
        let mutable cache : option<'A * IHashSetReader<'B>> = None
            
        override x.InputChangedObject(_, i) =
            if System.Object.ReferenceEquals(i, input) then
                valChanged <- 1

        override x.Compute(token) =
            let newValue = input.GetValue token
            #if FABLE_COMPILER
            let valChanged = let v = valChanged in valChanged <- 0; v = 1
            #else
            let valChanged = System.Threading.Interlocked.Exchange(&valChanged, 0) = 1
            #endif 

            match cache with
            | Some(oldValue, oldReader) when valChanged && not (cheapEqual oldValue newValue) ->
                // input changed
                let rem = CountingHashSet.removeAll oldReader.State
                oldReader.Outputs.Remove x |> ignore
                let newReader = (mapping newValue).GetReader()
                let add = newReader.GetChanges token
                cache <- Some(newValue, newReader)
                HashSetDelta.combine rem add

            | Some(_, ro) ->    
                // input unchanged
                ro.GetChanges token

            | None ->
                // initial
                let r = (mapping newValue).GetReader()
                cache <- Some(newValue, r)
                r.GetChanges token

    /// Reader for flattenA
    type FlattenAReader<'T>(input : aset<aval<'T>>) =
        inherit AbstractDirtyReader<aval<'T>, HashSetDelta<'T>>(HashSetDelta.monoid, isNull)
            
        let r = input.GetReader()
        do r.Tag <- "Input"

        let mutable initial = true
        let cache = UncheckedDictionary.create<aval<'T>, 'T>()

        member x.Invoke(token : AdaptiveToken, m : aval<'T>) =
            let v = m.GetValue token
            cache.[m] <- v
            v

        member x.Invoke2(token : AdaptiveToken, m : aval<'T>) =
            let o = cache.[m]
            let v = m.GetValue token
            cache.[m] <- v
            o, v

        member x.Revoke(m : aval<'T>, dirty : System.Collections.Generic.HashSet<_>) =
            match cache.TryGetValue m with
            | (true, v) -> 
                cache.Remove m |> ignore
                m.Outputs.Remove x |> ignore
                dirty.Remove m |> ignore
                v
            | _ -> 
                failwith "[ASet] cannot remove unknown object"

        override x.Compute(token, dirty) =
            let mutable deltas = 
                r.GetChanges token |> HashSetDelta.map (fun d ->
                    match d with
                    | Add(1,m) -> Add(x.Invoke(token, m))
                    | Rem(1,m) -> Rem(x.Revoke(m, dirty))
                    | _ -> unexpected()
                )

            for d in dirty do
                let o, n = x.Invoke2(token, d)
                if not (Unchecked.equals o n) then
                    deltas <- HashSetDelta.combine deltas (HashSetDelta.ofList [Add n; Rem o])

            deltas
            
    /// Reader for mapA
    type MapAReader<'A, 'B>(input : aset<'A>, mapping : 'A -> aval<'B>) =
        inherit AbstractDirtyReader<aval<'B>, HashSetDelta<'B>>(HashSetDelta.monoid, isNull)
            
        let reader = input.GetReader()
        do reader.Tag <- "Reader"
        let mapping = Cache mapping
        let cache = UncheckedDictionary.create<aval<'B>, ref<int * 'B>>()

        member x.Invoke(token : AdaptiveToken, v : 'A) =
            let m = mapping.Invoke v
            let v = m.GetValue token
            match cache.TryGetValue m with
            | (true, r) ->
                r := (fst !r + 1, v)
            | _ ->
                let r = ref (1, v)
                cache.[m] <- r

            v

        member x.Invoke2(token : AdaptiveToken, m : aval<'B>) =
            let r = cache.[m]
            let v = m.GetValue token
            let (rc, o) = !r
            r := (rc, v)
            o, v

        member x.Revoke(v : 'A, dirty : System.Collections.Generic.HashSet<_>) =
            let m = mapping.Revoke v
                
            match cache.TryGetValue m with
            | (true, r) -> 
                let (cnt, v) = !r
                if cnt = 1 then
                    cache.Remove m |> ignore
                    dirty.Remove m |> ignore
                    lock m (fun () -> m.Outputs.Remove x |> ignore )
                    v
                else
                    r := (cnt - 1, v)
                    v
            | _ -> 
                failwith "[ASet] cannot remove unknown object"

        override x.Compute(token, dirty) =
            let mutable deltas = 
                reader.GetChanges token |> HashSetDelta.map (fun d ->
                    match d with
                    | Add(1,m) -> Add(x.Invoke(token,m))
                    | Rem(1,m) -> Rem(x.Revoke(m, dirty))
                    | _ -> unexpected()
                )

            for d in dirty do
                let o, n = x.Invoke2(token, d)
                if not (Unchecked.equals o n) then
                    deltas <- HashSetDelta.combine deltas (HashSetDelta.ofList [Add n; Rem o])

            deltas
            
    /// Reader for chooseA
    type ChooseAReader<'A, 'B>(input : aset<'A>, f : 'A -> aval<option<'B>>) =
        inherit AbstractDirtyReader<aval<option<'B>>, HashSetDelta<'B>>(HashSetDelta.monoid, isNull)
            
        let r = input.GetReader()
        do r.Tag <- "Reader"

        let f = Cache f
        let mutable initial = true
        let cache = UncheckedDictionary.create<aval<option<'B>>, ref<int * option<'B>>>()

        member x.Invoke(token : AdaptiveToken, v : 'A) =
            let m = f.Invoke v
            let v = m.GetValue token

            match cache.TryGetValue m with
            | (true, r) ->
                r := (fst !r + 1, v)
            | _ ->
                let r = ref (1, v)
                cache.[m] <- r

            v


        member x.Invoke2(token : AdaptiveToken, m : aval<option<'B>>) =
            match cache.TryGetValue m with
            | (true, r) ->
                let (rc, o) = !r
                let v = m.GetValue token
                r := (rc, v)
                o, v
            | _ ->
                None, None  

        member x.Revoke(v : 'A) =
            let m = f.Revoke v
            match cache.TryGetValue m with
            | (true, r) -> 
                let (rc, v) = !r
                if rc = 1 then
                    cache.Remove m |> ignore
                    lock m (fun () -> m.Outputs.Remove x |> ignore )
                else
                    r := (rc - 1, v)
                v
            | _ -> 
                failwith "[ASet] cannot remove unknown object"


        override x.Compute(token, dirty) =
            let mutable deltas = 
                r.GetChanges token |> HashSetDelta.choose (fun d ->
                    match d with
                    | Add(1,m) -> 
                        match x.Invoke(token,m) with
                        | Some v -> Some (Add v)
                        | None -> None

                    | Rem(1,m) ->
                        match x.Revoke m with
                        | Some v -> Some (Rem v)
                        | None -> None

                    | _ -> 
                        unexpected()
                )

            for d in dirty do
                match x.Invoke2(token, d) with
                | None, Some n ->
                    deltas <- 
                        deltas
                        |> HashSetDelta.add (Add n)

                | Some o, None ->
                    deltas <- 
                        deltas
                        |> HashSetDelta.add (Rem o)

                | Some o, Some n when not (Unchecked.equals o n) ->
                    deltas <- 
                        deltas
                        |> HashSetDelta.add (Rem o)
                        |> HashSetDelta.add (Add n)

                | _ ->
                    ()

            deltas
 
    /// Gets the current content of the aset as HashSet.
    let inline force (set : aset<'T>) = 
        AVal.force set.Content

    /// Creates a constant set using the creation function.
    let inline constant (content : unit -> HashSet<'T>) = 
        ConstantSet(lazy(content())) :> aset<_> 

    /// Creates an adaptive set using the reader.
    let inline create (reader : unit -> #IOpReader<HashSetDelta<'T>>) =
        AdaptiveHashSetImpl(fun () -> reader() :> IOpReader<_>) :> aset<_>

/// Functional operators for aset<_>
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ASet =
    open AdaptiveHashSetImplementation

    /// The empty aset.
    [<GeneralizableValue>]
    let empty<'T> : aset<'T> = 
        EmptySet<'T>.Instance

    /// A constant aset holding a single value.
    let single (value : 'T) =
        lazy (HashSet.single value) |> ConstantSet :> aset<_>
        
    /// Creates an aset holding the given values.
    let ofSeq (s : seq<'T>) =
        lazy (HashSet.ofSeq s) |> ConstantSet :> aset<_>
        
    /// Creates an aset holding the given values.
    let ofList (s : list<'T>) =
        lazy (HashSet.ofList s) |> ConstantSet :> aset<_>
        
    /// Creates an aset holding the given values.
    let ofArray (s : 'T[]) =
        lazy (HashSet.ofArray s) |> ConstantSet :> aset<_>
        
    /// Creates an aset holding the given values. `O(1)`
    let ofHashSet (elements : HashSet<'T>) =
        ConstantSet(lazy elements) :> aset<_>

    /// Creates an aval providing access to the current content of the set.
    let toAVal (set : aset<'T>) =
        set.Content

    /// Adaptively maps over the given set.
    let map (mapping : 'A -> 'B) (set : aset<'A>) =
        if set.IsConstant then
            constant (fun () -> set |> force |> HashSet.map mapping)
        else
            create (fun () -> MapReader(set, mapping))
          
    /// Adaptively chooses all elements returned by mapping.  
    let choose (mapping : 'A -> option<'B>) (set : aset<'A>) =
        if set.IsConstant then
            constant (fun () -> set |> force |> HashSet.choose mapping)
        else
            create (fun () -> ChooseReader(set, mapping))
            
    /// Adaptively filters the set using the given predicate.
    let filter (predicate : 'A -> bool) (set : aset<'A>) =
        if set.IsConstant then
            constant (fun () -> set |> force |> HashSet.filter predicate)
        else
            create (fun () -> FilterReader(set, predicate))
            
    /// Adaptively unions the given sets
    let union (a : aset<'A>) (b : aset<'A>) =
        if a = b then
            a
        elif a.IsConstant && b.IsConstant then
            let va = force a
            let vb = force b
            if va.IsEmpty && vb.IsEmpty then empty
            else constant (fun () -> HashSet.union va vb)
        else
            // TODO: can be optimized in case one of the two sets is constant.
            create (fun () -> UnionConstantReader (HashSet.ofList [a;b]))

    /// Adaptively unions all the given sets
    let unionMany (sets : aset<aset<'A>>) = 
        if sets.IsConstant then
            // outer set is constant
            let all = force sets
            if all |> HashSet.forall (fun s -> s.IsConstant) then
                // all inner sets are constant
                constant (fun () -> all |> HashSet.collect force)
            else
                // inner sets non-constant
                create (fun () -> UnionConstantReader all)
        else
            // sadly no way to know if inner sets will always be constants.
            create (fun () -> UnionReader sets)

    /// Adaptively maps over the given set and unions all resulting sets.
    let collect (mapping : 'A -> aset<'B>) (set : aset<'A>) =   
        if set.IsConstant then
            // outer set is constant
            let all = set |> force |> HashSet.map mapping
            if all |> HashSet.forall (fun s -> s.IsConstant) then
                // all inner sets are constant
                constant (fun () -> all |> HashSet.collect force)
            else
                // inner sets non-constant
                create (fun () -> UnionConstantReader all)
        else
            create (fun () -> CollectReader(set, mapping))

    /// Creates an aset for the given aval.
    let ofAVal (value : aval<#seq<'T>>) =
        if value.IsConstant then
            constant (fun () -> AVal.force value :> seq<'T> |> HashSet.ofSeq)
        else
            create (fun () -> AValReader(value))

    /// Adaptively maps over the given aval and returns the resulting set.
    let bind (mapping : 'A -> aset<'B>) (value : aval<'A>) =
        if value.IsConstant then
            value |> AVal.force |> mapping
        else
            create (fun () -> BindReader(value, mapping))

    /// Adaptively flattens the set of adaptive avals.
    let flattenA (set : aset<aval<'A>>) =
        if set.IsConstant then
            let all = set |> force
            if all |> HashSet.forall (fun r -> r.IsConstant) then
                constant (fun () -> all |> HashSet.map AVal.force)
            else
                // TODO: better implementation possible
                create (fun () -> FlattenAReader(set))
        else
            create (fun () -> FlattenAReader(set))
            
    /// Adaptively maps over the set and also respects inner changes.
    let mapA (mapping : 'A -> aval<'B>) (set : aset<'A>) =
        // TODO: constants
        create (fun () -> MapAReader(set, mapping))

    /// Adaptively maps over the set and also respects inner changes.
    let chooseA (mapping : 'A -> aval<option<'B>>) (set : aset<'A>) =
        // TODO: constants
        create (fun () -> ChooseAReader(set, mapping))

    /// Adaptively filters the set and also respects inner changes.
    let filterA (predicate : 'A -> aval<bool>) (set : aset<'A>) =
        // TODO: direct implementation
        create (fun () -> 
            let mapping (a : 'A) =  
                predicate a 
                |> AVal.map (function true -> Some a | false -> None)

            ChooseAReader(set, mapping)
        )

    /// Adaptively folds over the set using add for additions and trySubtract for removals.
    /// Note the trySubtract may return None indicating that the result needs to be recomputed.
    /// Also note that the order of elements given to add/trySubtract is undefined.
    let foldHalfGroup (add : 'S -> 'A -> 'S) (trySub : 'S -> 'A -> option<'S>) (zero : 'S) (s : aset<'A>) =
        let r = s.GetReader()
        let mutable res = zero

        let rec traverse (d : list<SetOperation<'A>>) =
            match d with
                | [] -> true
                | d :: rest ->
                    match d with
                    | Add(1,v) -> 
                        res <- add res v
                        traverse rest

                    | Rem(1,v) ->
                        match trySub res v with
                        | Some s ->
                            res <- s
                            traverse rest
                        | None ->
                            false
                    | _ -> 
                        failwithf "[ASet] unexpected delta: %A" d
                                    

        AVal.custom (fun token ->
            let ops = r.GetChanges token
            let worked = traverse (HashSetDelta.toList ops)

            if not worked then
                res <- r.State |> CountingHashSet.fold add zero
                
            res
        )

    /// Adaptively folds over the set using add for additions and recomputes the value on every removal.
    /// Note that the order of elements given to add is undefined.
    let fold (f : 'S -> 'A -> 'S) (seed : 'S) (s : aset<'A>) =
        foldHalfGroup f (fun _ _ -> None) seed s

    /// Adaptively folds over the set using add for additions and subtract for removals.
    /// Note that the order of elements given to add/subtract is undefined.
    let foldGroup (add : 'S -> 'A -> 'S) (sub : 'S -> 'A -> 'S) (zero : 'S) (s : aset<'A>) =
        foldHalfGroup add (fun a b -> Some (sub a b)) zero s

    /// Creates an aset using the given reader-creator.
    let ofReader (creator : unit -> #IOpReader<HashSetDelta<'T>>) =
        create creator

    /// Creates a constant aset lazy content.
    let delay (creator : unit -> HashSet<'T>) =
        constant creator

    /// Adaptively computes the sum all entries in the set.
    let inline sum (set : aset<'A>) =
        foldGroup (+) (-) LanguagePrimitives.GenericZero set

    /// Adaptively computes the product of all entries in the set.
    let inline product (set : aset<'T>) =
        foldGroup (*) (/) LanguagePrimitives.GenericOne set
