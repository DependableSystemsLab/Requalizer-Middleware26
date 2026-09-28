"""
Reference implementation for DIFT Dataflow Verification (RQ2: Correctness).

This script provides a discrete-event simulation of the DIFT label propagation 
and routing behavior described in the paper. It evaluates different node architectures
(Standard, Load Balanced, Label-Aware Load Balanced) under different dynamic conditions.
"""

import argparse
import random
from collections import deque, Counter
from enum import Enum

class NodeType(Enum):
    STANDARD = "standard"       # Broadcasts to all outgoing edges
    LOAD_BALANCER = "lb"        # Sends to exactly one outgoing edge (randomly)
    LABEL_AWARE_LB = "label_lb" # Routes to node with matching label
    SINK = "sink"               # Terminal node

class NodeStatus(Enum):
    ALIVE = "alive"
    DEAD = "dead"

class Message:
    def __init__(self, msg_id: int, label: str):
        self.msg_id = msg_id
        self.label = label

class Node:
    def __init__(self, name: str, security_label: str, node_type: NodeType = NodeType.STANDARD, node_status: NodeStatus = NodeStatus.ALIVE):
        self.name = name
        self.security_label = security_label
        self.node_type = node_type
        self.status = node_status
        self.outgoing_edges = []
        self.label_stats = Counter()
        self.total_messages_processed = 0

    def connect_to(self, other_node: 'Node'):
        self.outgoing_edges.append(other_node)

    def process_message(self, message: Message):
        self.label_stats[message.label] += 1
        self.total_messages_processed += 1
        destinations = []

        if not self.outgoing_edges:
            return []

        if self.node_type == NodeType.LOAD_BALANCER:
            candidate_nodes = [node for node in self.outgoing_edges if node.status == NodeStatus.ALIVE]
            if candidate_nodes:
                destinations.append(random.choice(candidate_nodes))

        elif self.node_type == NodeType.LABEL_AWARE_LB:
            matching_nodes = [node for node in self.outgoing_edges if node.security_label == message.label and node.status == NodeStatus.ALIVE]
            if matching_nodes:
                destinations.append(random.choice(matching_nodes))
        
        elif self.node_type == NodeType.STANDARD:
            destinations = self.outgoing_edges
            
        return destinations

class Simulator:
    def __init__(self):
        self.nodes = {}
        self.entry_points = []

    def add_node(self, name: str, security_label: str, node_type: NodeType = NodeType.STANDARD, node_status: NodeStatus = NodeStatus.ALIVE) -> Node:
        node = Node(name, security_label, node_type, node_status)
        self.nodes[name] = node
        return node

    def add_edge(self, from_name: str, to_name: str):
        if from_name in self.nodes and to_name in self.nodes:
            self.nodes[from_name].connect_to(self.nodes[to_name])
        else:
            raise ValueError(f"Node names not found: {from_name} -> {to_name}")

    def set_entry_point(self, node_name: str):
        self.entry_points.append(self.nodes[node_name])

    def run(self, num_messages: int, possible_labels: list):
        print(f"--- Starting Simulation: {num_messages} messages ---")
        processing_queue = deque()

        for i in range(num_messages):
            label = random.choice(possible_labels)
            msg = Message(i, label)
            for entry_node in self.entry_points:
                processing_queue.append((entry_node, msg))

            while processing_queue:
                current_node, current_msg = processing_queue.popleft()
                next_nodes = current_node.process_message(current_msg)
                for next_node in next_nodes:
                    processing_queue.append((next_node, current_msg))

    def print_stats(self):
        print("\n--- Node Statistics (Label Distribution) ---")
        print(f"{'Node Name':<20} | {'Type':<12} | {'Sec Label':<10} | {'Total':<6} | Distribution")
        print("-" * 85)
        for name, node in self.nodes.items():
            dist_str = ", ".join([f"{k}: {v}" for k, v in sorted(node.label_stats.items())])
            print(f"{name:<20} | {node.node_type.value:<12} | {node.security_label:<10} | {node.total_messages_processed:<6} | {dist_str}")


def run_aal(mode='unaware stable', num_messages=10000):
    sim = Simulator()

    if mode == 'aware stable':
        sim.add_node("VideoCamera", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("FallDetector", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("FD-Balancer", "MEDIUM",  NodeType.LABEL_AWARE_LB)
        sim.add_node("RecordManager", "HIGH",  NodeType.STANDARD)
        sim.add_node("Notifier 1", "HIGH",  NodeType.SINK)
        sim.add_node("Notifier 2", "HIGH",  NodeType.SINK)
        sim.add_node("Notifier 3", "MEDIUM",  NodeType.SINK)
        sim.add_node("Notifier 4", "LOW",  NodeType.SINK)
        sim.add_node("AIDoctor","HIGH",  NodeType.STANDARD)
        sim.add_node("WebServer","HIGH",  NodeType.SINK)
    elif mode == 'unaware stable':
        sim.add_node("VideoCamera", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("FallDetector", "HIGH",  NodeType.STANDARD)
        sim.add_node("FD-Balancer", "HIGH",  NodeType.LOAD_BALANCER)
        sim.add_node("RecordManager", "HIGH",  NodeType.STANDARD)
        sim.add_node("Notifier 1", "MEDIUM",  NodeType.SINK)
        sim.add_node("Notifier 2", "HIGH",  NodeType.SINK)
        sim.add_node("Notifier 3", "LOW",  NodeType.SINK)
        sim.add_node("Notifier 4", "LOW",  NodeType.SINK)
        sim.add_node("AIDoctor","HIGH",  NodeType.STANDARD)
        sim.add_node("WebServer","LOW",  NodeType.SINK)
    elif mode == 'unaware crash':
        sim.add_node("VideoCamera", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("FallDetector", "LOW",  NodeType.STANDARD)
        sim.add_node("FD-Balancer", "LOW",  NodeType.LOAD_BALANCER)
        sim.add_node("RecordManager", "HIGH",  NodeType.STANDARD)
        sim.add_node("Notifier 1", "MEDIUM",  NodeType.SINK)
        sim.add_node("Notifier 2", "HIGH",  NodeType.SINK)
        sim.add_node("Notifier 3", "LOW",  NodeType.SINK)
        sim.add_node("Notifier 4", "LOW",  NodeType.SINK)
        sim.add_node("AIDoctor","HIGH",  NodeType.STANDARD)
        sim.add_node("WebServer","LOW",  NodeType.SINK)
    elif mode == 'unaware congestion':
        sim.add_node("VideoCamera", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("FallDetector", "LOW",  NodeType.STANDARD)
        sim.add_node("FD-Balancer", "LOW",  NodeType.LOAD_BALANCER)
        sim.add_node("RecordManager", "HIGH",  NodeType.STANDARD)
        sim.add_node("Notifier 1", "MEDIUM",  NodeType.SINK, NodeStatus.DEAD)
        sim.add_node("Notifier 2", "HIGH",  NodeType.SINK)
        sim.add_node("Notifier 3", "LOW",  NodeType.SINK)
        sim.add_node("Notifier 4", "LOW",  NodeType.SINK)
        sim.add_node("AIDoctor","HIGH",  NodeType.STANDARD)
        sim.add_node("WebServer","LOW",  NodeType.SINK)

    sim.add_edge("VideoCamera", "FallDetector")
    sim.add_edge("FallDetector", "FD-Balancer")
    sim.add_edge("FD-Balancer", "Notifier 1")
    sim.add_edge("FD-Balancer", "Notifier 2")
    sim.add_edge("FD-Balancer", "Notifier 3")
    sim.add_edge("FD-Balancer", "Notifier 4")
    sim.add_edge("FallDetector", "RecordManager")
    sim.add_edge("RecordManager", "AIDoctor")
    sim.add_edge("AIDoctor", "WebServer")

    sim.set_entry_point("VideoCamera")
    sim.run(num_messages, ["LOW", "MEDIUM", "HIGH"])
    sim.print_stats()


def run_fd(mode='aware stable', num_messages=10000):
    sim = Simulator()
    
    if mode == 'aware stable':
        sim.add_node("TransactionParser", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("TP-Balancer", "MEDIUM",  NodeType.LABEL_AWARE_LB)
        sim.add_node("Predictor 1", "HIGH",  NodeType.SINK)
        sim.add_node("Predictor 2", "HIGH",  NodeType.SINK)
        sim.add_node("Predictor 3", "MEDIUM",  NodeType.SINK)
        sim.add_node("Predictor 4", "LOW",  NodeType.SINK)
    elif mode == 'unaware stable':
        sim.add_node("TransactionParser", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("TP-Balancer", "MEDIUM",  NodeType.LOAD_BALANCER)
        sim.add_node("Predictor 1", "MEDIUM",  NodeType.SINK)
        sim.add_node("Predictor 2", "HIGH",  NodeType.SINK)
        sim.add_node("Predictor 3", "LOW",  NodeType.SINK)
        sim.add_node("Predictor 4", "LOW",  NodeType.SINK)
    elif mode == 'unaware crash':
        sim.add_node("TransactionParser", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("TP-Balancer", "MEDIUM",  NodeType.LOAD_BALANCER)
        sim.add_node("Predictor 1", "MEDIUM",  NodeType.SINK, NodeStatus.DEAD)
        sim.add_node("Predictor 2", "HIGH",  NodeType.SINK)
        sim.add_node("Predictor 3", "LOW",  NodeType.SINK)
        sim.add_node("Predictor 4", "LOW",  NodeType.SINK)
    elif mode == 'unaware congestion':
        sim.add_node("TransactionParser", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("TP-Balancer", "MEDIUM",  NodeType.LOAD_BALANCER)
        sim.add_node("Predictor 1", "MEDIUM",  NodeType.SINK, NodeStatus.DEAD)
        sim.add_node("Predictor 2", "HIGH",  NodeType.SINK)
        sim.add_node("Predictor 3", "LOW",  NodeType.SINK)
        sim.add_node("Predictor 4", "LOW",  NodeType.SINK)

    sim.add_edge("TransactionParser", "TP-Balancer")
    sim.add_edge("TP-Balancer", "Predictor 1")
    sim.add_edge("TP-Balancer", "Predictor 2")
    sim.add_edge("TP-Balancer", "Predictor 3")
    sim.add_edge("TP-Balancer", "Predictor 4")

    sim.set_entry_point("TransactionParser")
    sim.run(num_messages, ["LOW", "MEDIUM", "HIGH"])
    sim.print_stats()


def run_sg(mode='aware stable', num_messages=10000):
    sim = Simulator()
    
    if mode == 'aware stable':
        sim.add_node("SmartPlug", "LOW",  NodeType.STANDARD)
        sim.add_node("SlidingWindow", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("SW-Balancer", "MEDIUM",  NodeType.LABEL_AWARE_LB)
        sim.add_node("HouseLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugMedian 1", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 2", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 3", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("PlugMedian 4", "LOW",  NodeType.STANDARD)
        sim.add_node("GlobalMedian", "HIGH",  NodeType.STANDARD)
        sim.add_node("OutlierDetector", "HIGH",  NodeType.SINK)
    elif mode == 'unaware stable':
        sim.add_node("SmartPlug", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("SlidingWindow", "LOW",  NodeType.STANDARD)
        sim.add_node("SW-Balancer", "LOW",  NodeType.LOAD_BALANCER)
        sim.add_node("HouseLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugMedian 1", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 2", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 3", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("PlugMedian 4", "LOW",  NodeType.STANDARD)
        sim.add_node("GlobalMedian", "LOW",  NodeType.STANDARD)
        sim.add_node("OutlierDetector", "LOW",  NodeType.SINK)
    elif mode == 'unaware crash':
        sim.add_node("SmartPlug", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("SlidingWindow", "LOW",  NodeType.STANDARD)
        sim.add_node("SW-Balancer", "LOW",  NodeType.LOAD_BALANCER)
        sim.add_node("HouseLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugMedian 1", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 2", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 3", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("PlugMedian 4", "LOW",  NodeType.STANDARD)
        sim.add_node("GlobalMedian", "LOW",  NodeType.STANDARD)
        sim.add_node("OutlierDetector", "LOW",  NodeType.SINK)
    elif mode == 'unaware congestion':
        sim.add_node("SmartPlug", "MEDIUM",  NodeType.STANDARD)
        sim.add_node("SlidingWindow", "LOW",  NodeType.STANDARD)
        sim.add_node("SW-Balancer", "LOW",  NodeType.LOAD_BALANCER)
        sim.add_node("HouseLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugLoadPredictor", "MEDIUM",  NodeType.SINK)
        sim.add_node("PlugMedian 1", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 2", "HIGH",  NodeType.STANDARD)
        sim.add_node("PlugMedian 3", "MEDIUM",  NodeType.STANDARD, NodeStatus.DEAD)
        sim.add_node("PlugMedian 4", "LOW",  NodeType.STANDARD)
        sim.add_node("GlobalMedian", "LOW",  NodeType.STANDARD)
        sim.add_node("OutlierDetector", "LOW",  NodeType.SINK)

    sim.add_edge("SmartPlug", "SlidingWindow")
    sim.add_edge("SmartPlug", "HouseLoadPredictor")
    sim.add_edge("SmartPlug", "PlugLoadPredictor")
    sim.add_edge("SlidingWindow", "SW-Balancer")
    sim.add_edge("SW-Balancer", "PlugMedian 1")
    sim.add_edge("SW-Balancer", "PlugMedian 2")
    sim.add_edge("SW-Balancer", "PlugMedian 3")
    sim.add_edge("SW-Balancer", "PlugMedian 4")
    sim.add_edge("SlidingWindow", "GlobalMedian")
    sim.add_edge("GlobalMedian", "OutlierDetector")
    sim.add_edge("PlugMedian 1", "OutlierDetector")
    sim.add_edge("PlugMedian 2", "OutlierDetector")
    sim.add_edge("PlugMedian 3", "OutlierDetector")
    sim.add_edge("PlugMedian 4", "OutlierDetector")

    sim.set_entry_point("SmartPlug")
    sim.run(num_messages, ["LOW", "MEDIUM", "HIGH"])
    sim.print_stats()


def main():
    parser = argparse.ArgumentParser(description="DIFT Simulator")
    parser.add_argument('--app', choices=['AAL', 'FD', 'SPG'], default='AAL', help="Application to simulate")
    parser.add_argument('--mode', choices=['aware stable', 'unaware stable', 'unaware crash', 'unaware congestion'], default='aware stable', help="Mode of execution")
    parser.add_argument('--messages', type=int, default=10000, help="Number of messages to simulate")
    args = parser.parse_args()

    print(f"--- Running {args.app} ({args.mode}) ---")
    if args.app == 'AAL':
        run_aal(args.mode, args.messages)
    elif args.app == 'FD':
        run_fd(args.mode, args.messages)
    elif args.app == 'SPG':
        run_sg(args.mode, args.messages)

if __name__ == "__main__":
    main()
