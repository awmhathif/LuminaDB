"""Hold a TCP port open (to simulate another program, e.g. Skype/IIS/another XAMPP, on our port).
usage: python -I blocker.py <host> <port>   (host 127.0.0.1 or 0.0.0.0)"""
import socket, sys, time
s = socket.socket(); s.bind((sys.argv[1], int(sys.argv[2]))); s.listen(5)
print("holding", sys.argv[1], sys.argv[2], flush=True)
while True:
    time.sleep(1)
