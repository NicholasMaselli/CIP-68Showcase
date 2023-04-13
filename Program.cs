﻿using CardanoSharp.Wallet.CIPs.CIP2;
using CardanoSharp.Wallet.CIPs.CIP2.Models;
using CardanoSharp.Wallet.Extensions;
using CardanoSharp.Wallet.Extensions.Models.Transactions;
using CardanoSharp.Wallet.Models;
using CardanoSharp.Wallet.Models.Addresses;
using CardanoSharp.Wallet.Models.Transactions;
using CardanoSharp.Wallet.Models.Transactions.TransactionWitness.PlutusScripts;
using CardanoSharp.Wallet.TransactionBuilding;
using CardanoSharp.Wallet.Utilities;
using TestEnvironment.Setup;

// 1) Generate a Wallet or use an existing one
Wallet wallet = new Wallet();
wallet.GenerateWallet();

// 2) Initialize Blockfrost and the Policy Id
BlockfrostService blockfrostService = new BlockfrostService();
await blockfrostService.Initialize();

DateTime lockTime = DateTime.Now.AddYears(1);
var nativeScript = WalletService.GetPolicyScriptBuilder(wallet, lockTime);
var policyId = WalletService.GetPolicyId(wallet, lockTime).ToStringHex();

// 3) Initialize Address and Asset Name Data
string assetName = "HelloWorld";
string address = wallet.address!;
string scriptAddress = "addr1w93wm24j6fcesyfs8ed733jvct3pf2mazt7l8kxrew6ldzccmj96w";

Address paymentAddressObject = new Address(address!);
Address scriptAddressObject = new Address(scriptAddress);

// 4) Create Transaction Body
TransactionBodyBuilder transactionBodyBuilder = (TransactionBodyBuilder)
    TransactionBodyBuilder.Create;

// 5) Create User and Reference Asset Name Tokens
string referenceAssetNameHexPrefix = AssetLabelUtility.GetAssetLabelHex(100); // Asset Label (100) in CIP-67 format;
string userAssetNameHexPrefix = AssetLabelUtility.GetAssetLabelHex(222); // Asset Label (222) in CIP-67 format;

string userAssetNameHex = $"{userAssetNameHexPrefix}{assetName}";
string referenceAssetNameHex = $"{referenceAssetNameHexPrefix}{assetName}";
string referenceAssetFullNameHex = $"{policyId}{referenceAssetNameHex}";

// 6) Calculate CIP-68 Mint NFTs
TokenBundleBuilder totalMintTokenBundleBuilder = (TokenBundleBuilder)TokenBundleBuilder.Create;
TokenBundleBuilder referenceMintTokenBundleBuilder = (TokenBundleBuilder)TokenBundleBuilder.Create;
TokenBundleBuilder userMintTokenBundleBuilder = (TokenBundleBuilder)TokenBundleBuilder.Create;

totalMintTokenBundleBuilder.AddToken(
    policyId.HexToByteArray(),
    referenceAssetNameHex.HexToByteArray(),
    1
);
totalMintTokenBundleBuilder.AddToken(
    policyId.HexToByteArray(),
    userAssetNameHex.HexToByteArray(),
    1
);
referenceMintTokenBundleBuilder.AddToken(
    policyId.HexToByteArray(),
    referenceAssetNameHex.HexToByteArray(),
    1
);
userMintTokenBundleBuilder.AddToken(
    policyId.HexToByteArray(),
    userAssetNameHex.HexToByteArray(),
    1
);

// 7) Calculate Datums
// Calculate the Reference NFT Datum Metadata - Update this field to add data for your NFT!
// Plutus Data 6.121[metadata, version, extra]
// 6.121[metadata, version, extra] = constructor 0, fields [{}, 1, #6.121([])]
PlutusDataMap metadata = new PlutusDataMap();
PlutusDataInt version = new PlutusDataInt() { Value = 1 };
PlutusDataConstr extra = new PlutusDataConstr()
{
    Value = new PlutusDataArray() { Value = new IPlutusData[] { } },
    Alternative = 0
};

IPlutusData[] metadataDatum = new IPlutusData[] { metadata, version, extra };
PlutusDataArray metadataDatumArray = new PlutusDataArray() { Value = metadataDatum };
PlutusDataConstr constr = new PlutusDataConstr() { Value = metadataDatumArray, Alternative = 0 };
DatumOption datum = new DatumOption() { Data = constr };

// 8) Create Transaction Outputs

// 8.1) Create Reference Token Output
TransactionOutputBuilder transactionReferenceOutputBuilder = (TransactionOutputBuilder)
    TransactionOutputBuilder.Create
        .SetAddress(scriptAddress.ToBytes())
        .SetTransactionOutputValue(
            new TransactionOutputValue
            {
                Coin = (ulong)(5 * CardanoService.adaToLovelace),
                MultiAsset = referenceMintTokenBundleBuilder.Build()
            }
        )
        .SetDatumOption(datum);

var transactionScriptOutput = transactionReferenceOutputBuilder.Build();
transactionReferenceOutputBuilder.SetTransactionOutputValue(
    new TransactionOutputValue
    {
        Coin = transactionScriptOutput.CalculateMinUtxoLovelace(),
        MultiAsset = referenceMintTokenBundleBuilder.Build()
    }
);

// 8.2) Create User Token Output
transactionBodyBuilder.AddOutput(
    paymentAddressObject.GetBytes(),
    userMintTokenBundleBuilder.Build().CalculateMinUtxoLovelace(),
    userMintTokenBundleBuilder
);

// 9) Select Inputs and change using the largest first Coin Selection Algorithm
List<Utxo> utxos = await blockfrostService.GetUTXOs(
    new List<string> { paymentAddressObject.ToString() }
);
CoinSelection? coinSelection = transactionBodyBuilder.UseLargestFirst(
    utxos,
    address,
    mint: totalMintTokenBundleBuilder,
    feeBuffer: (ulong)CardanoService.adaOnlyMinUTXO
);
if (coinSelection == null)
{
    throw new Exception("Coin Selection Error");
}

foreach (TransactionOutput changeOutput in coinSelection.ChangeOutputs)
    transactionBodyBuilder.AddOutput(changeOutput);

foreach (TransactionInput input in coinSelection.Inputs)
    transactionBodyBuilder.AddInput(input);

// 10) Build the Transaction
transactionBodyBuilder
    .SetMint(totalMintTokenBundleBuilder)
    .SetTtl(blockfrostService.blockfrostData.currentSlot + 1 * 60 * 15) // 15 minutes
    .SetFee(0);

// 11) Create Transaction Witnesses
TransactionWitnessSetBuilder transactionWitnessSetBuilder = (TransactionWitnessSetBuilder)
    TransactionWitnessSetBuilder.Create;
transactionWitnessSetBuilder
    .AddVKeyWitness(wallet.publicKey, wallet.privateKey)
    .SetScriptAllNativeScript(nativeScript);

TransactionBuilder transactionBuilder = (TransactionBuilder)TransactionBuilder.Create;
transactionBuilder.SetBody(transactionBodyBuilder).SetWitnesses(transactionWitnessSetBuilder);
Transaction transaction = transactionBuilder.Build();

// 12) Calculate Final Fee
var fee = transaction.CalculateAndSetFee(
    blockfrostService.blockfrostData.minFeeA,
    blockfrostService.blockfrostData.minFeeB
);

ulong lastOutputChangeBalance = transaction.TransactionBody.TransactionOutputs.Last().Value.Coin;
transaction.TransactionBody.TransactionOutputs.Last().Value.Coin =
    lastOutputChangeBalance - (ulong)fee;

// 13) Submit Transaction
string? transactionId = await blockfrostService.SubmitTransaction(transaction);
if (transactionId != null)
    Console.WriteLine(
        $"Transaction Submitted Successfully! TransactionId: {transactionId}. TransactionIndex: 0"
    );
else
    Console.WriteLine($"Transaction Failed!");
