# Realistic Warhead

Ein C#-Server-Plugin (LabAPI) für einen internationalen Multiplayer-Server (SCP:SL).  
Über den Admin-Befehl `purge` lässt sich eine **Alpha-** oder Omega-Warhead auslösen. Dabei startet ein Countdown mit Musik und es werden begleitende Events getriggert (u. a. alle Türen öffnen). Erreicht der Timer T=0, wird der Spieler sterben, falls sie nicht enkommen sind.

## Befehle
- `purge warhead arm alpha` - startet die Alpha-Warhead-Sequenz  
- `purge warhead arm omega` - startet die Omega-Warhead-Sequenz  

## Escape
Während der Alpha, bzw. Omega-Warhead-Sequenz, wird auf der "Surface Zone", ein Evacuation spot gespawneed, was dann auch automatisch verschwindet, wenn ein bestimmer Zeitpunkt erreicht wurde.

## Dekontamination
Enthält zusätzlich eine Dekontaminations-Funktion, mit der sich eine eigene Decontamination-Sequenz (Audio + Events) abspielen lässt.

## Demo
Ein Kurzvideo mit der Sequenz stelle ich auf Anfrage gern bereit.
