import React, { Component } from 'react';
import axios from 'axios';
import { BASE_URL } from './constants';

import {
    Card,
    Button
} from 'semantic-ui-react';

import TransferList from './TransferList';

class TransferGroup extends Component {
    state = { selections: new Set() }

    onSelectionChange = (directoryName, file, selected) => {
        const { selections } = this.state;
        const obj = JSON.stringify({ directory: directoryName, filename: file.filename });
        selected ? selections.add(obj) : selections.delete(obj);

        this.setState({ selections });
    }

    isSelected = (directoryName, file) => 
        this.state.selections.has(JSON.stringify({ directory: directoryName, filename: file.filename }));

    getSelectedFiles = () => {
        const { user } = this.props;
        
        return Array.from(this.state.selections)
            .map(s => JSON.parse(s))
            .map(s => user.directories
                .find(d => d.directory === s.directory)
                .files.find(f => f.filename === s.filename)
            ).filter(s => s !== undefined);
    }

    removeFileSelection = (file) => {
        const { selections } = this.state;

        const match = Array.from(selections)
            .map(s => JSON.parse(s))
            .find(s => s.filename === file.filename);

        if (match) {
            selections.delete(JSON.stringify(match));
            this.setState({ selections });
        }
    }

    isStateRetryable = (state) => this.props.direction === 'download' && state.includes('Completed') && state !== 'Completed, Succeeded';
    isStateCancellable = (state) => ['InProgress', 'Requested', 'Queued', 'Initializing'].find(s => s === state);
    isStateRemovable = (state) => state.includes('Completed');

    retryAll = async (direction, username, selected) => {
        await Promise.all(selected.map(file => axios.post(`${BASE_URL}/transfers/downloads/${username}/${encodeURI(file.filename)}`)));
    }

    cancelAll = async (direction, username, selected) => {
        await Promise.all(selected.map(file => axios.delete(`${BASE_URL}/transfers/${direction}s/${username}/${encodeURI(file.filename)}`)));
    }

    removeAll = async (direction, username, selected) => {
        await Promise.all(selected.map(file => 
                axios.delete(`${BASE_URL}/transfers/${direction}s/${username}/${encodeURI(file.filename)}?remove=true`)
                    .then(() => this.removeFileSelection(file))));
    }
    
    render = () => {
        const { user, direction } = this.props;

        const selected = this.getSelectedFiles();
        const all = selected.length > 1 ? ' Selected' : '';
        
        const allRetryable = selected.filter(f => this.isStateRetryable(f.state)).length === selected.length;
        const anyCancellable = selected.filter(f => this.isStateCancellable(f.state)).length > 0;
        const allRemovable = selected.filter(f => this.isStateRemovable(f.state)).length === selected.length;

        return (
            <Card key={user.username} className='transfer-card' raised>
                <Card.Content>
                    <Card.Header>{user.username}</Card.Header>
                    {user.directories && user.directories
                        .map((dir, index) => 
                        <TransferList 
                            key={index} 
                            username={user.username} 
                            directoryName={dir.directory}
                            files={(dir.files || []).map(f => ({ ...f, selected: this.isSelected(dir.directory, f) }))}
                            onSelectionChange={this.onSelectionChange}
                            direction={this.props.direction}
                        />
                    )}
                </Card.Content>
                {selected && selected.length > 0 && 
                <Card.Content extra>
                    {<Button.Group>
                        {allRetryable && 
                        <Button 
                            icon='redo' 
                            color='green' 
                            content={`Retry${all}`} 
                            onClick={() => this.retryAll(direction, user.username, selected)}
                        />}
                        {allRetryable && anyCancellable && <Button.Or/>}
                        {anyCancellable && 
                        <Button 
                            icon='x'
                            color='red'
                            content={`Cancel${all}`}
                            onClick={() => this.cancelAll(direction, user.username, selected)}
                        />}
                        {(allRetryable || anyCancellable) && allRemovable && <Button.Or/>}
                        {allRemovable && 
                        <Button 
                            icon='delete'
                            content={`Remove${all}`}
                            onClick={() => this.removeAll(direction, user.username, selected)}
                        />}
                    </Button.Group>}
                </Card.Content>}
            </Card>
        );
    }
}

export default TransferGroup;