﻿namespace CardManagement

module CardWorkflow =
    open CardDomain
    open System
    open CardDomainCommandModels
    open CardManagement.Common
    open CardDomainQueryModels
    open Errors

    type Program<'a> =
        | GetCard of CardNumber * (Card option -> Program<'a>)
        | GetCardWithAccountInfo of CardNumber * ((Card*AccountInfo) option -> Program<'a>)
        | CreateCard of (Card*AccountInfo) * (Result<unit, DataRelatedError> -> Program<'a>)
        | ReplaceCard of Card * (Result<unit, DataRelatedError> -> Program<'a>)
        | GetUser of UserId * (User option -> Program<'a>)
        | CreateUser of UserInfo * (Result<unit, DataRelatedError> -> Program<'a>)
        | GetBalanceOperations of (CardNumber * DateTimeOffset * DateTimeOffset) * (BalanceOperation list -> Program<'a>)
        | SaveBalanceOperation of BalanceOperation * (Result<unit, DataRelatedError> -> Program<'a>)
        | Stop of 'a

    let rec bind f instruction =
        match instruction with
        | GetCard (x, next) -> GetCard (x, (next >> bind f))
        | GetCardWithAccountInfo (x, next) -> GetCardWithAccountInfo (x, (next >> bind f))
        | CreateCard (x, next) -> CreateCard (x, (next >> bind f))
        | ReplaceCard (x, next) -> ReplaceCard (x, (next >> bind f))
        | GetUser (x, next) -> GetUser (x,(next >> bind f))
        | CreateUser (x, next) -> CreateUser (x,(next >> bind f))
        | GetBalanceOperations (x, next) -> GetBalanceOperations (x,(next >> bind f))
        | SaveBalanceOperation (x, next) -> SaveBalanceOperation (x,(next >> bind f))
        | Stop x -> f x

    let stop x = Stop x
    let getCard number = GetCard (number, stop)
    let getCardWithAccountInfo number = GetCardWithAccountInfo (number, stop)
    let createCard (card, acc) = CreateCard ((card, acc), stop)
    let replaceCard card = ReplaceCard (card, stop)
    let getUser id = GetUser (id, stop)
    let saveUser user = CreateUser (user, stop)
    let getBalanceOperations (number, fromDate, toDate) = GetBalanceOperations ((number, fromDate, toDate), stop)
    let saveBalanceOperation op = SaveBalanceOperation (op, stop)

    type ProgramBuilder() =
        member __.Bind (x, f) = bind f x
        member this.Bind (x, f) =
            match x with
            | Ok x -> this.ReturnFrom (f x)
            | Error e -> this.Return (Error e)
        member this.Bind((x: Program<Result<_,_>>), f) =
            let f x =
                match x with
                | Ok x -> this.ReturnFrom (f x)
                | Error e -> this.Return (Error e )
            this.Bind(x, f)
        member __.Return x = Stop x
        member __.Zero () = Stop ()
        member __.ReturnFrom x = x

    let program = ProgramBuilder()

    let expectDataRelatedErrorProgram (p: Program<Result<_,DataRelatedError>>) =
        program {
            let! result = p
            return expectDataRelatedError result
        }

    let private noneToError (a: 'a option) id =
        let error = EntityNotFound (sprintf "%sEntity" typeof<'a>.Name, id)
        Result.ofOption error a

    let processPayment (currentDate: DateTimeOffset) payment =
        program {
            let! cmd = validateProcessPaymentCommand payment |> expectValidationError
            let! card = getCard cmd.CardNumber
            let! card = noneToError card cmd.CardNumber.Value |> expectDataRelatedError
            let today = currentDate.Date |> DateTimeOffset
            let tomorrow = currentDate.Date.AddDays 1. |> DateTimeOffset
            let! operations = getBalanceOperations (cmd.CardNumber, today, tomorrow)
            let spentToday = BalanceOperation.spentAtDate currentDate cmd.CardNumber operations
            let! (card, op) =
                CardActions.processPayment currentDate spentToday card cmd.PaymentAmount
                |> expectOperationNotAllowedError
            do! saveBalanceOperation op |> expectDataRelatedErrorProgram
            do! replaceCard card |> expectDataRelatedErrorProgram
            return card |> toCardInfoModel |> Ok
        }

    let setDailyLimit (currentDate: DateTimeOffset) setDailyLimitCommand =
        program {
            let! cmd = validateSetDailyLimitCommand setDailyLimitCommand |> expectValidationError
            let! card = getCard cmd.CardNumber
            let! card = noneToError card cmd.CardNumber.Value |> expectDataRelatedError
            let! card = CardActions.setDailyLimit currentDate cmd.DailyLimit card |> expectOperationNotAllowedError
            do! replaceCard card |> expectDataRelatedErrorProgram
            return card |> toCardInfoModel |> Ok
        }

    let topUp (currentDate: DateTimeOffset) topUpCmd =
        program {
            let! cmd = validateTopUpCommand topUpCmd |> expectValidationError
            let! card = getCard cmd.CardNumber
            let! card = noneToError card cmd.CardNumber.Value |> expectDataRelatedError
            let! (card, op) = CardActions.topUp currentDate card cmd.TopUpAmount |> expectOperationNotAllowedError
            do! saveBalanceOperation op |> expectDataRelatedErrorProgram
            do! replaceCard card |> expectDataRelatedErrorProgram
            return card |> toCardInfoModel |> Ok
        }

    let activateCard activateCmd =
        program {
            let! cmd = validateActivateCardCommand activateCmd |> expectValidationError
            let! result = getCardWithAccountInfo cmd.CardNumber
            let! (card, accInfo) = noneToError result cmd.CardNumber.Value |> expectDataRelatedError
            let card = CardActions.activate accInfo card
            do! replaceCard card |> expectDataRelatedErrorProgram
            return card |> toCardInfoModel |> Ok
        }

    let deactivateCard deactivateCmd =
        program {
            let! cmd = validateDeactivateCardCommand deactivateCmd |> expectValidationError
            let! card = getCard cmd.CardNumber
            let! card = noneToError card cmd.CardNumber.Value |> expectDataRelatedError
            let card = CardActions.deactivate card
            do! replaceCard card |> expectDataRelatedErrorProgram
            return card |> toCardInfoModel |> Ok
        }