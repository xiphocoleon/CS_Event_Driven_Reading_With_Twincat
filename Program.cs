using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using System.Buffers.Binary;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Drawing.Imaging;

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
 * 
 * 
 */

//Variable for state machine case
int machineState = -1;
bool firstScan = true;
string systemStatus = "";

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

/*
    NetworkStream ns = client.GetStream(); //networkstream is used to send/receive messages

    byte[] hello = new byte[100];   //any message must be serialized (converted to byte array)
    hello = Encoding.Default.GetBytes("hello world");  //conversion string => byte array

    ns.Write(hello, 0, hello.Length);     //sending the message

    while (client.Connected)  //while the client is connected, we look for incoming messages
    {
        byte[] msg = new byte[1024];     //the messages arrive as byte array
        ns.Read(msg, 0, msg.Length);   //the same networkstream reads the message sent by the client
        Console.WriteLine(encoder.GetString(msg).Trim('')); //now , we write the message as string
    }
}
*/


/*once connection open
I request specs
they request the specs
i send lot and wafer #
use s for send

send g for get results

ping to make sure keeping alive
use a dual nic
one nic to internal network
one nic to our machine

*/


//Main Loop
for (;;)
{
    //If Client is connected, send message
    if(client.Connected)
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
            NetworkStream ns2 = client.GetStream();
            //Send g for Get Data
            commandByte = Encoding.Default.GetBytes("g");  
            ns2.Write(commandByte, 0, commandByte.Length);
            machineState = 300;
            break;
        case 300:
            //Create network stream for send/receive messages
            NetworkStream ns3 = client.GetStream();
            Thread.Sleep(10);
            int recipeLength = ns3.Read(recipe, 0, recipe.Length);
            Console.WriteLine()
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
        // Or here we know about UDINT type --> can be marshalled as UINT32
        //Don't think I need this:
        //uint nCounter = BinaryPrimitives.ReadUInt32LittleEndian(e.Data.Span);
        // If Synchronization is needed (e.g. in Windows.Forms or WPF applications)
        // we could synchronize via SynchronizationContext into the UI Thread

        /*SynchronizationContext syncContext = SynchronizationContext.Current;
          _context.Post(status => someLabel.Text = nCounter.ToString(), null); // Non-blocking post */

        Console.WriteLine("The PLC barcode data updated and this lot code update event occurred");
        CancellationToken cancel2 = CancellationToken.None;
        AdsClient clientOnLotUpdate = new AdsClient();
        clientOnLotUpdate.Connect(AmsNetId.Local, 851);
        uint notificationHandleLotCode = 0;
        int sizeLotCode = sizeof(UInt32);
        notificationHandleLotCode = clientOnLotUpdate.CreateVariableHandle("MAIN.sBarcodeLotNumber");
        //Print notification handle if needed
        //Console.WriteLine(notificationHandleLotCode);
        
        //Print Result String
        try
        {
            Console.WriteLine("Try matching the new barcode:");
            int byteSize = barcodeLength; // Max length of eMagin barcode
            PrimitiveTypeMarshaler converter = new PrimitiveTypeMarshaler(StringMarshaler.DefaultEncoding);
            byte[] buffer = new byte[byteSize];

            int readBytes = clientOnLotUpdate.Read(notificationHandleLotCode, buffer.AsMemory());

            /*
            Console.WriteLine("The read string is: " + buffer[0].ToString());
            Console.WriteLine("The read string is: " + buffer[1].ToString());
            Console.WriteLine("The read string is: " + buffer[2].ToString());
            */

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
            /*
            for(int i = 0; i < buffer.Length; i++)
            {
                Console.WriteLine("The read char is: " + Convert.ToChar(buffer[i]));
            }
            */
            string value = null;
            converter.Unmarshal<string>(buffer.AsSpan(), out value);

        }
        finally
        {
            clientOnLotUpdate.DeleteVariableHandle(notificationHandleLotCode);
        }
    }
}