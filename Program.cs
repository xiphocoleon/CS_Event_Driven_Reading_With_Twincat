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
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Formats.Asn1;

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
 * TO DO:
 * --Add check to verify PLC is running; if it's not, do smtg else
 * --Add check -- if barcode is not 8 char, or doesn't follow certain pattern, throw error
 *
 * NOTES FROM EMAGIN/CALL W KYLE M.:
    I request recipe
    they request the specs
    i send lot and wafer # (not sure right now how we get wafer #)
    use s for send

    send g for get results

    ping to make sure keeping alive
    they use a dual nic on their external computer; one connection is to this computer
*/

//Variable for state machine case
int machineState = -1;
bool firstScan = true;
string systemStatus = "";

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
byte[] recipe = new byte[3000];
for(int i = 0; i < recipe.Length; i++)
{
    recipe[i] = 0;
}
int recipeLength = 0;
string entireRecipe = "0";

//Initialize first scan of variables
if (firstScan)
{
    machineState = 0;
}

//Start the TCP server, which the eMagin system client will connect to
TcpListener server = new TcpListener(IPAddress.Any, 9999);
server.Start();

//If a connection exists, the server will accept it
TcpClient client = server.AcceptTcpClient();

//Instantiate the recipe class to prevent compiler errors
EmaginRecipe emaginRecipe = new EmaginRecipe();


//Main Program Loop with a statemachine
for (;;)
{
    //If Client is connected, send message
    if (client.Connected)
    {
        Console.WriteLine("eMagin system has connected.");
    }

    switch (machineState)
    {
        //Create new PLC update object, which waits for a barcode scan/update to the Lot Code
        //This will advance to next state when new Lot Code detected
        case 0:
            PLCUpdate plcUpdate = new PLCUpdate(barcodeLength, initialByteArray, firstScan);

            // This will enter a loop to wait for any new lot code from the PLC:
            await plcUpdate.RegisterNotificationsAsync();

            // Use Encoding.UTF8.GetString method
            lotCode = Encoding.UTF8.GetString(plcUpdate.updatedBarcodeByte);

            //lotCode = plcUpdate.updatedBarcodeByte[0].ToString();
            machineState = 200;
            break;

        //With the barcode obtained in previous state, connect to eMagin system via TCP IP and send Lot Code
        case 200:
            firstScan = false;
            systemStatus = String.Format("Machine State {0}: Current lot code is {1}. Waiting for eMagin system connection.", machineState, lotCode);
            Console.WriteLine(systemStatus);
            Thread.Sleep(500);
            machineState = 210;
            break;
        case 210:
            //Create network stream for send/receive messages
            NetworkStream ns = client.GetStream();
            byte[] commandByte = new byte[2];
            commandByte = Encoding.Default.GetBytes("s");  //ASCII s == decimal 115
            ns.Write(commandByte, 0, commandByte.Length);
            Thread.Sleep(10);
            byte[] lotCodeByte = new byte[barcodeLength];
            lotCodeByte = Encoding.Default.GetBytes(lotCode);
            ns.Write(lotCodeByte, 0, lotCodeByte.Length);
            machineState = 220;
            
            break;
        case 220:
            systemStatus = String.Format("Machine State {0}: Sent lot code {1} to eMagin. Now will send request to eMagin for recipe.", machineState, lotCode);
            Console.WriteLine(systemStatus);
            //Get the stream
            ns = client.GetStream();
            //Send g for Get Data
            commandByte = Encoding.Default.GetBytes("g");  
            ns.Write(commandByte, 0, commandByte.Length);
            machineState = 300;
            break;
        case 300:
            Console.WriteLine("Waiting to get response with recipe from eMagin system.");
            //Get the stream
            ns = client.GetStream();
            Thread.Sleep(10);
            recipeLength = ns.Read(recipe, 0, recipe.Length);
            if(recipeLength > 0)
            {
                Console.WriteLine("The length of the recipe is: {0}", recipe.Length);
                Console.WriteLine("The recipe is: {0}", Encoding.UTF8.GetString(recipe));
                entireRecipe = Encoding.UTF8.GetString(recipe);
            }
            machineState = 400;
            break;
        case 400:
            //Trim null characters \0 or 0x00 from the JSON data or it will give an error when deserializing:
            entireRecipe = entireRecipe.Trim('\0');
            Console.WriteLine("The received JSON to deserialize is: " + entireRecipe);

            emaginRecipe = JsonSerializer.Deserialize<EmaginRecipe>(entireRecipe)!;

            //Print 3 example values:
            Console.WriteLine($"After Deserializing the Material ID is: {emaginRecipe.Material_ID}");
            Console.WriteLine($"ULVAC #: {emaginRecipe.ULVAC}");
            Console.WriteLine($"Lot Number: {emaginRecipe.LOT_NUMBER}");
            Console.WriteLine("Sending data to PLC");
            Thread.Sleep(10);
            machineState = 410;
            break;
        case 410:
            //In this state we need to update tags on the PLC
            //Create a new instance of class TcAdsClient
            AdsClient tcClient = new AdsClient();
            AdsStream dataStream = new AdsStream(4);

            uint iHandle = 0;
            int iValue = emaginRecipe.Material_ID;

            try
            {
                // Connect to local PLC - Runtime 1 - TwinCAT 3 Port=851
                tcClient.Connect(851);

                //Get the handle of the PLC variable
                iHandle = tcClient.CreateVariableHandle("MAIN.Material_ID");

                //do
                //{
                    /* I COULD DO A CHECK LATER TO READ THE PLC VALUE AND ONLY UPDATE UNDER CERTAIN CONDITIONS
                    //Use the handle to read PLCVar
                    tcClient.Read(iHandle, dataStream);
                    iValue = binReader.ReadInt32();
                    dataStream.Position = 0;

                    Console.WriteLine("Current value is: " + iValue);
                    */
    
                //Write values to PLC
                tcClient.WriteAny(iHandle, emaginRecipe.Material_ID);

                //Disconnect from PLC
                tcClient.Disconnect();

                //Go to state to wait for PLC's message the run is starting
                machineState = 420;

                //} while (Console.ReadKey().Key.Equals(ConsoleKey.Enter));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.ReadKey();
            }
            finally
            {
                //Delete variable handle
                tcClient.DeleteVariableHandle(iHandle);
                tcClient.Dispose();
            }
            break;

        //Wait for PLC to say it will start run
        case 420:
            //In this state we need to see the tag on the PLC for "running" go high
            //Create a new instance of class TcAdsClient
            AdsClient tcClient2 = new AdsClient();
            AdsStream dataStream2 = new AdsStream(4);

            uint iHandleRunStart = 0;
            bool bRunStart = false;

            // Connect to local PLC - Runtime 1 - TwinCAT 3 Port=851
            tcClient2.Connect(851);

            //Get the handle of the PLC variable
            iHandleRunStart = tcClient2.CreateVariableHandle("MAIN.bRunStart");

            /*
            //Use the handle to read PLCVar
            tcClient2.Read(iHandleRunStart, );
            iValue = binReader.ReadInt32();
            dataStream.Position = 0;
            */
            Console.WriteLine("Current value is: " + iValue);

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

        using (AdsClient client = new AdsClient())
        {
            //Add the Notification event handler
            client.AdsNotification += Client_AdsNotification;

            //Connect to target
            Console.WriteLine("Trying to connect to PLC ADS port 851: ");
            client.Connect(AmsNetId.Local, 851);

            //Symbol connection data
            uint notificationHandle = 0;
            int size = sizeof(UInt32);
            ResultHandle result = await client.AddDeviceNotificationAsync("MAIN.sBarcodeLotNumber", size, new NotificationSettings(AdsTransMode.OnChange, 200, 0), null, cancel);
            Console.WriteLine("Connected to PLC and linked to lot code string: ");

            //On first scan, put initialByteArray into updatedBarcodeByte
            updatedBarcodeByte = initialByteArray;

            //Check if barcode data is the same as previous barcode data
            //If they are the same, do not update; otherwise, vice versa
            for (int i = 0; ( !(barcodeUpdated) && i < initialByteArray.Length ); i++)
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


            while(!(barcodeUpdated))
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
                ResultAds result2 = await client.DeleteDeviceNotificationAsync(notificationHandle, cancel);
            }
            //client.AdsNotification -= Client_AdsNotification2;
        }
    }

    public void Client_AdsNotification(object sender, AdsNotificationEventArgs e)
    {
        Console.WriteLine("The PLC barcode data updated and this lot code update event occurred");
        CancellationToken cancel2 = CancellationToken.None;
        AdsClient clientOnLotUpdate = new AdsClient();
        clientOnLotUpdate.Connect(AmsNetId.Local, 851);
        uint notificationHandleLotCode = 0;
        int sizeLotCode = sizeof(UInt32);
        notificationHandleLotCode = clientOnLotUpdate.CreateVariableHandle("MAIN.sBarcodeLotNumber");
        
        //Print Result String
        try
        {
            Console.WriteLine("Try matching the new barcode:");
            int byteSize = barcodeLength; // Max length of eMagin barcode
            PrimitiveTypeMarshaler converter = new PrimitiveTypeMarshaler(StringMarshaler.DefaultEncoding);
            byte[] buffer = new byte[byteSize];

            int readBytes = clientOnLotUpdate.Read(notificationHandleLotCode, buffer.AsMemory());

            //On first scan, but contents of buffer into updatedBarcodeByte
            if(firstScan==true)
            {
                for(int i = 0; i < buffer.Length; i++)
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
            clientOnLotUpdate.DeleteVariableHandle(notificationHandleLotCode);
        }
    }
}

public class EmaginRecipe
{
    public int Material_ID { get; set; }
    public string ULVAC { get; set; }
    public string LOT_NUMBER { get; set; }
    public string WAFER_NUMBER { get; set; }
    public string PRODUCT { get; set; }
    public string COLOR { get; set; }
    public string OWNER { get; set; }
    public string TSMC { get; set; }
    public string STACK { get; set; }
    public string ANODE { get; set; }
    public string DATE_RELEASED { get; set; }
    public string EA_NUMBER { get; set; }
    public string LID_TYPE { get; set; }
    public int ID1 { get; set; }
    public int ID { get; set; }
    public float MIN_LUM { get; set; }
    public string PRODUCT_TYPE { get; set; }
    public float DARK_CUR { get; set; }
    public float LUM_LSL { get; set; }
    public int DIMCTL { get; set; }
    public int VGMAX { get; set; }
    public float LUM_TYP { get; set; }
    public float LUM_USL { get; set; }
    public float BRIGHT_CIE_X_LSL { get; set; }
    public int IDRF_LSL { get; set; }
    public int IDRF_USL { get; set; }
    public float BRIGHT_CIE_X_USL { get; set; }
    public float BRIGHT_CIE_Y_LSL { get; set; }
    public float BRIGHT_CIE_Y_USL { get; set; }
    public float RED_CIE_X_LSL { get; set; }
    public float RED_CIE_X_USL { get; set; }
    public float GREEN_CIE_X_LSL { get; set; }
    public float RED_CIE_Y_LSL { get; set; }
    public float RED_CIE_Y_USL { get; set; }
    public float GREEN_CIE_X_USL { get; set; }
    public float BLUE_CIE_X_LSL { get; set; }
    public float GREEN_CIE_Y_LSL { get; set; }
    public float GREEN_CIE_Y_USL { get; set; }
    public float BLUE_CIE_X_USL { get; set; }
    public float BLUE_CIE_Y_LSL { get; set; }
    public float BLUE_CIE_Y_USL { get; set; }
    public float SLOPE_LSL { get; set; }
    public float SLOPE_USL { get; set; }
    public float ORIG1_LSL { get; set; }
    public float ORIG1_USL { get; set; }
    public float VGN0_LSL { get; set; }
    public float VGN0_USL { get; set; }
    public float VGN1_LSL { get; set; }
    public float VGN1_USL { get; set; }
    public float VGN2_LSL { get; set; }
    public float VGN2_USL { get; set; }
    public float VGN3_LSL { get; set; }
    public float VGN3_USL { get; set; }
    public float VGN4_LSL { get; set; }
    public float VGN4_USL { get; set; }
    public float VGN5_LSL { get; set; }
    public float VGN5_USL { get; set; }
    public float VGN6_LSL { get; set; }
    public float VGN6_USL { get; set; }
    public float VGN7_LSL { get; set; }
    public float VGN7_USL { get; set; }
    public float LAU_LSL { get; set; }
    public float LOW_VGN0_LSL { get; set; }
    public float LOW_VGN0_USL { get; set; }
    public float LOW_VGN1_LSL { get; set; }
    public float LOW_VGN1_USL { get; set; }
    public float LOW_VGN2_LSL { get; set; }
    public float LOW_VGN2_USL { get; set; }
    public float LOW_VGN3_LSL { get; set; }
    public float LOW_VGN3_USL { get; set; }
    public float LOW_VGN4_LSL { get; set; }
    public float LOW_VGN4_USL { get; set; }
    public float LOW_VGN5_LSL { get; set; }
    public float LOW_VGN5_USL { get; set; }
    public float LOW_VGN6_LSL { get; set; }
    public float LOW_VGN6_USL { get; set; }
    public float LOW_VGN7_LSL { get; set; }
    public float LOW_VGN7_USL { get; set; }
    public float DISPOFF_5V_I_LSL { get; set; }
    public float DISPOFF_5V_I_USL { get; set; }
    public float DISPOFF_2V5_I_LSL { get; set; }
    public float DISPOFF_2V5_I_USL { get; set; }
    public float DISPOFF_1V8_I_LSL { get; set; }
    public float DISPOFF_1V8_I_USL { get; set; }
    public float GL0_5V_I_LSL { get; set; }
    public float GL0_5V_I_USL { get; set; }
    public float GL0_4V_I_LSL { get; set; }
    public float GL0_4V_I_USL { get; set; }
    public float GL0_3V3_I_LSL { get; set; }
    public float GL0_3V3_I_USL { get; set; }
    public float GL0_2V5_I_LSL { get; set; }
    public float GL0_2V5_I_USL { get; set; }
    public float GL0_1V8_I_LSL { get; set; }
    public float GL0_1V8_I_USL { get; set; }
    public float GL0_VCOM_I_LSL { get; set; }
    public float GL0_VCOM_I_USL { get; set; }
    public float GL255_5V_I_LSL { get; set; }
    public float GL255_5V_I_USL { get; set; }
    public float GL255_4V_I_LSL { get; set; }
    public float GL255_4V_I_USL { get; set; }
    public float GL255_3V3_I_LSL { get; set; }
    public float GL255_3V3_I_USL { get; set; }
    public float GL255_2V5_I_LSL { get; set; }
    public float GL255_2V5_I_USL { get; set; }
    public float GL255_1V8_I_LSL { get; set; }
    public float GL255_1V8_I_USL { get; set; }
    public float GL255_VCOM_I_LSL { get; set; }
    public float GL255_VCOM_I_USL { get; set; }
}
