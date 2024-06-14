using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using System.Buffers.Binary;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using System.Net;
using System.Net.Sockets;
using System.Drawing.Imaging;
using System.Formats.Asn1;
using System.ComponentModel.DataAnnotations;
using System.Reflection.Metadata;

/*
 * 
 * FLOWCHART:
 * 
 * 0.      
 *          TCP Server is started; program will not advance until eMagin system starts a session with it
 *     
 *          CS listener program is running, waiting for PLC to send barcode
 * 100.     Barcode (sBarcodeLotNumber) is scanned by PLC and listener program receives it
 * 150      CS program stops waiting for lot code
 * 200.     CS program sends lot code value to eMagin via TCP/IP
 * 300.     eMagin system responds with recipe data
 * 400.     CS program sends data to PLC
 * 500.     PLC says it got the data and that run will start
 * 600.     PLC does the run; CS program not accepting any commands
 * 700.     PLC reports it is done. JSON file is created. CS program returns to state 0
 * 
 * 
 *
 * SOME NOTES FROM EMAGIN/CALL W KYLE M.:
    I request recipe
    they request the specs
    i send lot and wafer # (not sure right now how we get wafer #)
    use s for send

    send g for get results

    ping to make sure keeping alive
    they use a dual nic on their external computer; one connection is to this computer
*/

//Variable for state machine case
int inputProgramState = -1;
string inputProgramStatus = "Default";
bool firstScan = true;
string inputProgramStatusToConsole= "Default";
//PLC Run Status Variables
uint iHandleRunStart = 0;
uint iHandleRunComplete = 0;
bool bRunStart = false;
bool bRunComplete = false;

//JSON Deserialization Options
var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};

//Variables for eMagin system
int barcodeLength = 8;
byte[] initialByteArray = new byte[barcodeLength];
//Console.WriteLine("The initial byte array length is: " + initialByteArray.Length.ToString());
string lotCode = "";
//Recipe byte intialized -- find out max size
byte[] recipe = new byte[5000];
for (int i = 0; i < recipe.Length; i++)
{
    recipe[i] = 0;
}
int recipeLength = 0;
string entireRecipe = "0";

//Initialize first scan of variables
if (firstScan)
{
    inputProgramState = 0;
}

//Create AdsClient for exchanging data between this program and PLC tags
AdsClient clientPLC = new AdsClient();
//AdsStream dataStream = new AdsStream(4);

Console.WriteLine("Waiting for eMagin system to connect.");
clientPLC.Connect(851);
inputProgramStatus = "Waiting for eMagin system to connect.";
clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);

//Start the TCP server, which the eMagin system client will connect to
TcpListener server = new TcpListener(IPAddress.Any, 9999);
server.Start();
inputProgramState = 1;

//If a connection exists, the server will accept it
TcpClient clientEmagin = server.AcceptTcpClient();

//Main Program Loop with a statemachine
for (; ; )
{
    //If Client is connected, send message
    if (clientEmagin.Connected)
    {
        Console.WriteLine("eMagin system is connected.");
    }

    switch (inputProgramState)
    {
        //Create new PLC update object, which waits for a barcode scan/update to the Lot Code
        //This will advance to next state when new Lot Code detected
        case 1:
            //clientPLC.Connect(851);
            inputProgramStatus = "Ready and waiting for a Lot Code to be scanned or entered";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);

            PLCUpdate plcUpdate = new PLCUpdate(barcodeLength, initialByteArray, firstScan);

            // This will enter a loop to wait for any new lot code from the PLC:
            await plcUpdate.RegisterNotificationsAsync();

            // Use Encoding.UTF8.GetString method
            lotCode = Encoding.UTF8.GetString(plcUpdate.updatedBarcodeByte);

            //lotCode = plcUpdate.updatedBarcodeByte[0].ToString();
            inputProgramState = 200;
            break;

        //With the barcode obtained in previous state, connect to eMagin system via TCP IP and send Lot Code
        case 200:
            firstScan = false;
            inputProgramStatus = "Waiting for eMagin system connection";
            inputProgramStatusToConsole = String.Format("Machine State {0}: Current lot code is {1}. Waiting for eMagin system connection.", inputProgramState, lotCode);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            Console.WriteLine(inputProgramStatusToConsole);
            Thread.Sleep(500);
            inputProgramState = 210;
            break;
        case 210:
            //Create network stream for send/receive messages
            inputProgramStatus = "About to send eMagin System S + Lot Code";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            NetworkStream ns = clientEmagin.GetStream();
            byte[] commandByte = new byte[2];
            commandByte = Encoding.Default.GetBytes("s");  //ASCII s == decimal 115
            ns.Write(commandByte, 0, commandByte.Length);
            Thread.Sleep(10);
            byte[] lotCodeByte = new byte[barcodeLength];
            lotCodeByte = Encoding.Default.GetBytes(lotCode);
            ns.Write(lotCodeByte, 0, lotCodeByte.Length);
            inputProgramState = 220;

            break;
        case 220:
            inputProgramStatus = "Sent Lot Code to eMagin";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            inputProgramStatusToConsole = String.Format("Machine State {0}: Sent lot code {1} to eMagin. Now will send request to eMagin for recipe.", inputProgramState, lotCode);
            Console.WriteLine(inputProgramStatusToConsole);
            //Get the stream
            ns = clientEmagin.GetStream();
            //Send g for Get Data
            commandByte = Encoding.Default.GetBytes("g");
            ns.Write(commandByte, 0, commandByte.Length);
            inputProgramState = 300;
            break;
        case 300:
            inputProgramStatus = "Waiting to get recipe from eMagin";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            Console.WriteLine("Waiting to get response with recipe from eMagin system.");
            //Get the stream
            ns = clientEmagin.GetStream();
            Thread.Sleep(10);
            recipeLength = ns.Read(recipe, 0, recipe.Length);
            if (recipeLength > 0)
            {
                Console.WriteLine("The length of the recipe is: {0}", recipe.Length);
                Console.WriteLine("The recipe is: {0}", Encoding.UTF8.GetString(recipe));
                entireRecipe = Encoding.UTF8.GetString(recipe);
            }
            inputProgramState = 400;
            break;
        case 400:
            inputProgramStatus = "About to write recipe from eMagin to PLC";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            //Deserialize JSON recipe and write to PLC
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            //Trim null characters \0 or 0x00 from the JSON data or it will give an error when deserializing:
            entireRecipe = entireRecipe.Trim('\0');
            Console.WriteLine("The received JSON to deserialize is: " + entireRecipe);

            //Newtonsoft Deserializer:
            Dictionary<string, object> emaginRecipeDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(entireRecipe);

            //Create Value Tuple list that will hold Key and handle
            //Property name, property value, uint to get handle of variable on PLC
            List<ValueTuple<string, object, uint>> emaginKeyHandleTuple = new List<ValueTuple<string, object, uint>>();

            // Connect to local PLC - Runtime 1 - TwinCAT 3 Port=851
            clientPLC.Connect(851);
            try
            {
                //Write deserialized recipe and handle obtained from PLC
                foreach (var item in emaginRecipeDict)
                {
                    /*
                    Console.WriteLine("Key: " + item.Key + ", Value: " + item.Value + ", Handle: " + 
                        clientPLC.CreateVariableHandle("Main." + item.Key));
                    */
                    //emaginKeyHandleTuple.Add((item.Key, item.Value, clientPLC.CreateVariableHandle("Main." + item.Key)));
                    clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL." + item.Key), item.Value);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.ReadKey();
            }
            finally
            {
                //Delete variable handle if needed
                //clientPLC.DeleteVariableHandle(someHandle);
                //clientPLC.Dispose();
            }
            Console.WriteLine("Sent recipe data to PLC");
            Thread.Sleep(10);
            inputProgramState = 500;
            break;
        case 500:
            inputProgramStatus = "Waiting to for PLC to start run";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            //Wait for PLC to tell us it got the data and that it is going to do a run
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            //Get the handle of the PLC variable
            iHandleRunStart = clientPLC.CreateVariableHandle("GVL.bRunStart");

            //Use the handle to read PLCVar
            bRunStart = (bool)clientPLC.ReadAny(iHandleRunStart, typeof(System.Boolean));
            Thread.Sleep(1000);
            Console.WriteLine("Current value is of PLC run status is: " + bRunStart);

            if (bRunStart == true)
            {
                inputProgramState = 600;

            }

            break;

        //Wait for PLC to say it completed the run and that the JSON is created
        case 600:
            inputProgramStatus = "Waiting for PLC to say run is complete";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            //Get the handle of the PLC variable
            iHandleRunComplete = clientPLC.CreateVariableHandle("GVL.bRunComplete");

            //Use the handle to read PLCVar
            bRunComplete = (bool)clientPLC.ReadAny(iHandleRunComplete, typeof(System.Boolean));
            Thread.Sleep(1000);
            Console.WriteLine("Current value is of PLC run complete status is: " + bRunComplete);

            if (bRunComplete == true)
            {
                inputProgramState = 700;
            }

            break;
        //Report that PLC run is complete and that result JSON is created
        case 700:
            inputProgramStatus = "Notified that PLC run is complete will go back to beginning of sequence";
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.sInputProgramStatus"), inputProgramStatus);
            clientPLC.WriteAny(clientPLC.CreateVariableHandle("GVL.dStateMachineInputProgram"), inputProgramState);
            Thread.Sleep(1000);
            Console.WriteLine("PLC run is complete; check Reports folder for results JSON");
            inputProgramState = 1;
            break;
        default:
            break;

    }

}//End Forever loop

public class PLCUpdate(int barcodeLength, byte[] initialByteArray, bool firstScan)
{
    public byte[] updatedBarcodeByte = new byte[barcodeLength];
    public bool barcodeUpdated = false;

    public async Task RegisterNotificationsAsync()
    {
        CancellationToken cancel = CancellationToken.None;

        using (AdsClient clientPLCNotify = new AdsClient())
        {
            //Add the Notification event handler
            clientPLCNotify.AdsNotification += Client_AdsNotification;

            //Connect to target
            Console.WriteLine("Trying to connect to PLC ADS port 851: ");
            clientPLCNotify.Connect(AmsNetId.Local, 851);

            //Symbol connection data
            uint notificationHandle = 0;
            int size = sizeof(UInt32);
            ResultHandle result = await clientPLCNotify.AddDeviceNotificationAsync("GVL.sLotCode", size, new NotificationSettings(AdsTransMode.OnChange, 200, 0), null, cancel);
            Console.WriteLine("Connected to PLC and linked to lot code string: ");

            //On first scan, put initialByteArray into updatedBarcodeByte
            updatedBarcodeByte = initialByteArray;

            //Check if barcode data is the same as previous barcode data
            //If they are the same, do not update; otherwise, vice versa
            for (int i = 0; (!(barcodeUpdated) && i < initialByteArray.Length); i++)
            {
                if (updatedBarcodeByte[i] == initialByteArray[i])
                {
                    barcodeUpdated = false;
                    Console.WriteLine("Value {0} is the same", i);
                }
                else
                {
                    barcodeUpdated = true;
                    Console.WriteLine("Value {0} is the different; update activated", i);
                }
            }


            while (!(barcodeUpdated))
            {
                Console.WriteLine("Waiting for barcode scan");
                Thread.Sleep(200);
            }

            Console.WriteLine("The While Loop is over waiting for the updated barcode");

            //Not needed I think:
            if (result.Succeeded)
            {
                notificationHandle = result.Handle;
                await Task.Delay(5000); // Wait asynchronously without blocking the UI Thread.
                                        // Unregister the Event / Handle
                Console.WriteLine("Result was a success ");
                ResultAds result2 = await clientPLCNotify.DeleteDeviceNotificationAsync(notificationHandle, cancel);
            }
            //client.AdsNotification -= Client_AdsNotification2;
        }
    }

    public void Client_AdsNotification(object sender, AdsNotificationEventArgs e)
    {
        Console.WriteLine("The PLC barcode data updated and this lot code update event occurred");
        CancellationToken cancel2 = CancellationToken.None;
        AdsClient clientPLCOnLotUpdate = new AdsClient();
        clientPLCOnLotUpdate.Connect(AmsNetId.Local, 851);
        uint notificationHandleLotCode = 0;
        int sizeLotCode = sizeof(UInt32);
        notificationHandleLotCode = clientPLCOnLotUpdate.CreateVariableHandle("GVL.sLotCode");

        //Print Result String
        try
        {
            Console.WriteLine("Try matching the new barcode:");
            int byteSize = barcodeLength; // Max length of eMagin barcode
            PrimitiveTypeMarshaler converter = new PrimitiveTypeMarshaler(StringMarshaler.DefaultEncoding);
            byte[] buffer = new byte[byteSize];

            int readBytes = clientPLCOnLotUpdate.Read(notificationHandleLotCode, buffer.AsMemory());

            //On first scan, but contents of buffer into updatedBarcodeByte
            if (firstScan == true)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    updatedBarcodeByte[i] = buffer[i];
                    firstScan = false;
                }
            }

            //Check to see if new lot code is same as previous (do not update if it is)
            //If they are the same, do not update; otherwise, vice versa
            for (int i = 0; (!(barcodeUpdated) && i < updatedBarcodeByte.Length); i++)
            {
                if (buffer[i] == updatedBarcodeByte[i])
                {
                    barcodeUpdated = false;
                    Console.WriteLine("Buffer value {0} is: ", buffer[i]);
                    Console.WriteLine("updatedBarcodeByte value {0} is: ", updatedBarcodeByte[i]);

                    Console.WriteLine("Value {0} is the same", i);
                }
                else
                {
                    barcodeUpdated = true;
                    Console.WriteLine("Buffer value {0} is: ", buffer[i]);
                    Console.WriteLine("updatedBarcodeByte value {0} is: ", updatedBarcodeByte[i]);

                    Console.WriteLine("Value {0} is the different; update activated", i);

                    //If a difference was detected between barcodes, updateBarcodeUpdated with Buffer
                    for (int j = 0; j < buffer.Length; j++)
                    {
                        updatedBarcodeByte[j] = buffer[j];
                    }

                }
            }

            string value = null;
            converter.Unmarshal<string>(buffer.AsSpan(), out value);

        }
        finally
        {
            clientPLCOnLotUpdate.DeleteVariableHandle(notificationHandleLotCode);
        }
    }
}
