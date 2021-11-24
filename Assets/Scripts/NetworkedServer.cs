using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;

public class NetworkedServer : MonoBehaviour
{
    int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    int socketPort = 5491;
    LinkedList<PlayerAccount> playerAccounts;
    LinkedList<GameSession> gameSessions;

    private string playerAccountFilePath;
    private int playerWaitingForMatch  = -1;
    
    // Start is called before the first frame update
    void Start()
    {
        NetworkTransport.Init();
        ConnectionConfig config = new ConnectionConfig();
        reliableChannelID = config.AddChannel(QosType.Reliable);
        unreliableChannelID = config.AddChannel(QosType.Unreliable);
        HostTopology topology = new HostTopology(config, maxConnections);
        hostID = NetworkTransport.AddHost(topology, socketPort, null);
        playerAccounts = new LinkedList<PlayerAccount>();
        gameSessions = new LinkedList<GameSession>();
        playerAccountFilePath = Application.dataPath + Path.DirectorySeparatorChar + "PlayerAccountData.txt";
        
        LoadPlayerAccounts();
    }

    // Update is called once per frame
    void Update()
    {
        int recHostID;
        int recConnectionID;
        int recChannelID;
        byte[] recBuffer = new byte[1024];
        int bufferSize = 1024;
        int dataSize;
        byte error = 0;

        NetworkEventType recNetworkEvent = NetworkTransport.Receive(out recHostID, out recConnectionID, out recChannelID, recBuffer, bufferSize, out dataSize, out error);

        switch (recNetworkEvent)
        {
            case NetworkEventType.Nothing:
                break;
            case NetworkEventType.ConnectEvent:
                Debug.Log("Connection, " + recConnectionID);
                break;
            case NetworkEventType.DataEvent:
                string msg = Encoding.Unicode.GetString(recBuffer, 0, dataSize);
                ProcessRecievedMsg(msg, recConnectionID);
                break;
            case NetworkEventType.DisconnectEvent:
                Debug.Log("Disconnection, " + recConnectionID);
                break;
        }
    }
  
    public void SendMessageToClient(string msg, int id)
    {
        byte error = 0;
        byte[] buffer = Encoding.Unicode.GetBytes(msg);
        NetworkTransport.Send(hostID, id, reliableChannelID, buffer, msg.Length * sizeof(char), out error);
    }
    
    private void ProcessRecievedMsg(string msg, int id)
    {
        Debug.Log("msg recieved = " + msg + ".  connection id = " + id);

        string[] csv = msg.Split(',');

        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            string n = csv[1];
            string p = csv[2];

            bool isUnique = true;
            
            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                   isUnique = false;
                    break;
                }
            }
            if (isUnique)
            {
                playerAccounts.AddLast(new PlayerAccount(n, p));
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.Success, id);
                
                //save player account list
                SavePlayerAccounts();
            }
            else
            {
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureNameInUse, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.Login)
        {
            string n = csv[1];
            string p = csv[2];

            bool hasBeenFound = false;

            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == n)
                {
                    if (pa.password == p)
                    {
                        SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.Success, id);
                    }
                    else
                    {
                        SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureIncorrectPassword, id);
                    }
                    
                    hasBeenFound = true;
                    break;
                }
            }
            
            if (!hasBeenFound)
            {
                SendMessageToClient(ServerToClientSiginifiers.LoginResponse + "," + LoginResponses.FailureNameNotFound, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.AddToGameSessionQueue)
        {
            //if there is no player waiting, save the waiting player in the above variable
            if (playerWaitingForMatch == -1)
            {
                //make a single int variable to represent the one and only possible waiting player
                playerWaitingForMatch = id;   
            }
            else //if there is one waiting player, join the session
            {
                //Create the game session object, pass it to two players
                GameSession gs = new GameSession(playerWaitingForMatch, id);
                gameSessions.AddLast(gs);
                
                //Decide turn order & chess mark
                int ran = UnityEngine.Random.Range(1, 3);
                if (ran == 1)
                {
                    SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "," + gs.playerID1 + "," + 1 + "," + 1, gs.playerID1); 
                    SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "," + gs.playerID2 + "," + 2 + "," + 0, gs.playerID2); 
                }
                else
                {
                    SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "," + gs.playerID2 + "," + 1 + "," + 1, gs.playerID2);
                    SendMessageToClient(ServerToClientSiginifiers.GameSessionStarted + "," + gs.playerID1 + "," + 2 + "," + 0, gs.playerID1);
                }
                
                //Pass a signifier to both clients that they've joined one
                playerWaitingForMatch = -1;
            }
        }
        else if (signifier == ClientToServerSignifiers.playerAction)
        {
            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs != null)
            {
                if (gs.playerID1 == id)
                {
                    SendMessageToClient(ServerToClientSiginifiers.OpponentTicTacToePlay + "," + csv[1] + "," + csv[2], gs.playerID2);
                    gs.chessList.Add(new PlayerChess(int.Parse(csv[1]), int.Parse(csv[2])));
                }
                else
                {  
                    SendMessageToClient(ServerToClientSiginifiers.OpponentTicTacToePlay + "," + csv[1] + "," + csv[2], gs.playerID1);
                    gs.chessList.Add(new PlayerChess(int.Parse(csv[1]), int.Parse(csv[2])));
                }

                if (gs.spectatorList.Count > 0)
                { 
                    foreach (int spectator in gs.spectatorList)
                    {
                        SendMessageToClient(ServerToClientSiginifiers.updateSpectator + "," + csv[1] + "," + csv[2], spectator);
                    }
                }
            }
        }
        else if (signifier == ClientToServerSignifiers.sendMessage)
        {
            GameSession gs = FindGameSessionWithPlayerID(id);

            if (gs != null)
            {
                if (gs.playerID1 == id)
                {
                    SendMessageToClient(ServerToClientSiginifiers.DisplayReceivedMsg + "," + csv[1], gs.playerID2);
                }
                else
                {
                    SendMessageToClient(ServerToClientSiginifiers.DisplayReceivedMsg + "," + csv[1], gs.playerID1);
                }
            }
        }
        else if (signifier == ClientToServerSignifiers.watchGame) //get a random game session to join
        {
            if (gameSessions.Count <= 0)
            {
                // No session available
                Debug.Log("No session available");
            }
            else
            {
                List<int> templist = new List<int>();

                foreach (GameSession gs in gameSessions)
                {
                    templist.Add(gs.playerID1);
                    templist.Add(gs.playerID2);
                }
                
                int randomIndex = UnityEngine.Random.Range(0,  gameSessions.Count + 1);
                GameSession tempgs = FindGameSessionWithPlayerID(templist[randomIndex]);
                tempgs.spectatorList.Add(id);
                
                SendMessageToClient(ServerToClientSiginifiers.spectatorJoin + "," + 0, id); 

                if (tempgs.chessList.Count > 0) 
                {
                    foreach (PlayerChess pc in tempgs.chessList)
                    {
                        Debug.Log("Sending player chess");
                        SendMessageToClient(ServerToClientSiginifiers.spectatorJoin + "," + 1 + ","+ pc.chessPos + "," + pc.chessMark, id);
                    }
                    SendMessageToClient(ServerToClientSiginifiers.spectatorJoin + "," + 2, id);
                }
            }
        }
    }

    private void SavePlayerAccounts()
    {
        StreamWriter sw =
            new StreamWriter(playerAccountFilePath);
        
        foreach (PlayerAccount pa in playerAccounts)
        {
            sw.WriteLine(pa.name + "," + pa.password);
        }
        
        sw.Close();
    }

    private void LoadPlayerAccounts()
    {
        if (File.Exists(playerAccountFilePath))
        {
            StreamReader sr =
                new StreamReader(playerAccountFilePath);

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                string[] csv = line.Split(',');

                PlayerAccount pa = new PlayerAccount(csv[0], csv[1]);
                playerAccounts.AddLast(pa);
            }
        }
    }

    private GameSession FindGameSessionWithPlayerID(int id)
    {
        foreach (GameSession gs in gameSessions)
        {
            if (gs.playerID1 == id || gs.playerID2 == id)
                return gs;
        }
        
        return null;
    }
    
    public class PlayerAccount
    {
        public string name,password;
        
        public PlayerAccount(string name, string password)
        {
            this.name = name;
            this.password = password;
        }
    }

    public class GameSession
    {
        public int playerID1, playerID2; 
        public List<PlayerChess> chessList;
        public List<int> spectatorList;
        
        public GameSession(int playerID1, int playerID2)
        {
            this.playerID1 = playerID1;
            this.playerID2 = playerID2;
            chessList = new List<PlayerChess>();
            spectatorList = new List<int>();
        }

        public void AddPlayerChess(int mark, int pos)
        {
            chessList.Add(new PlayerChess(mark, pos));
        }
    }

    public class PlayerChess
    {
        public int chessMark;
        public int chessPos;

        public PlayerChess(int chessPos, int chessMark)
        {
            this.chessMark = chessMark;
            this.chessPos = chessPos;
        }
    }

    public static class ClientToServerSignifiers
    {
        public const int Login = 1;
        public const int CreateAccount = 2;
        public const int AddToGameSessionQueue = 3;
        public const int TicTacToePlay = 4;
        public const int playerAction = 5;
        public const int playerWin = 6;
        public const int isDraw = 7;
        public const int sendMessage = 8;
        public const int watchGame = 9;
    }

    public static class ServerToClientSiginifiers
    {
        public const int LoginResponse = 1;
        public const int GameSessionStarted = 2;
        public const int OpponentTicTacToePlay = 3;
        public const int DisplayReceivedMsg = 4;
        public const int DecideTurnOrder = 5;
        public const int spectatorJoin = 6;
        public const int updateSpectator = 7;
    }

    public static class LoginResponses
    {
        public const int Success = 1;
        public const int FailureNameInUse = 2;
        public const int FailureNameNotFound = 3;
        public const int FailureIncorrectPassword = 4; 
    }
}
