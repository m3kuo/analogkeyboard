var latestInputs = new Array();
var defaultWSI = "ws://127.0.0.1:32312/";
var knownWSI = "ws://127.0.0.1/"
var useDefault = true;

let websocket;

function connect() {
    const wsUri = useDefault ? defaultWSI : knownWSI;
    websocket = new WebSocket(wsUri);

    websocket.onopen = (e) => { console.log("Connected to server."); };

    websocket.onclose = (e) => {
        console.log("Socket is closed. Reconnect will be attempted in 3 seconds.");
        useDefault = !useDefault;
        resetKeys();
        setTimeout(connect, 3000);
        return;
    };

    websocket.onmessage = (e) => { update(`${e.data}`); };

    websocket.onerror = (e) => { console.log("Error.  " + e.data); };
}

function update(message) {
    if (message == "") {
        resetKeys();
        return;
    }

    if (message.includes("GESTURE:")) {
        const gesture = message.split("|").pop().replace("GESTURE:", "");
        document.getElementById("output").textContent = `Gesture detected: ${gesture}`;
    }
}

function stopGestureDetection() {
    if (websocket && websocket.readyState === WebSocket.OPEN) {
        websocket.send("STOP");
    }
}

function resetKeys() {
    document.getElementById("output").textContent = "";
}

connect();
